using System.Security.Cryptography;
using System.Text.Json;
using Bridge.DesktopCore;
using Bridge.Win32;
using Bridge.WindowsAgent.Contracts;

namespace Bridge.WindowsAgent.Backend;

/// <summary>
/// The real desktop backend (T13–T19). Screenshot and UIA run in
/// the isolated worker process (T14); SendInput runs here on a
/// serial queue (T16/T17); every input is journaled before its
/// side effect (T07 semantics, docs/04 §7) and re-checked against
/// the local pause/epoch immediately before executing (T19).
/// </summary>
public sealed class WindowsDesktopBackend : IDesktopBackend
{
    private readonly DesktopWorkerHost _worker;
    private readonly LocalControlState _control;
    private readonly CommandJournal _journal;
    private readonly SemaphoreSlim _inputQueue = new(1, 1);
    private readonly Dictionary<Guid, Observation> _observations = new();
    private readonly Dictionary<Guid, byte[]> _artifacts = new();
    private readonly List<ushort> _heldModifiers = new();
    private bool _disposed;

    public WindowsDesktopBackend(
        DesktopWorkerHost worker, LocalControlState control, string dataDirectory)
    {
        _worker = worker;
        _control = control;
        Directory.CreateDirectory(dataDirectory);
        _journal = new CommandJournal(Path.Combine(dataDirectory, "commands.db"));
    }

    // ---- T13: desktop + monitor state ----

    public async Task<DesktopCapabilities> ProbeAsync(CancellationToken ct)
    {
        var probe = await _worker.RequestAsync("probe", DesktopWorkerHost.ProbeTimeout, ct);
        var interactive = probe.GetProperty("interactive_session").GetBoolean();
        var unlocked = probe.GetProperty("desktop_unlocked").GetBoolean();
        var monitorCount = probe.GetProperty("monitor_count").GetInt32();
        var input = interactive && unlocked && !_control.Paused && !_worker.Degraded;
        var reason = _worker.Degraded ? "worker_degraded"
            : _control.Paused ? "local_paused"
            : !interactive ? "no_interactive_session"
            : !unlocked ? "desktop_locked"
            : "ok";
        return new DesktopCapabilities
        {
            BackendState = _worker.Degraded ? "degraded" : "ok",
            InteractiveSession = interactive,
            DesktopUnlocked = unlocked,
            Observe = interactive && unlocked,
            Input = input,
            Uia = interactive && unlocked && !_worker.Degraded,
            MultiMonitor = monitorCount > 1,
            Reason = reason,
        };
    }

    // ---- T15: constrained observation ----

    public async Task<Observation> ObserveAsync(
        InternalObservationRequest request, CancellationToken ct)
    {
        if (_control.Paused)
            throw new InvalidOperationException("local_paused: the desktop is paused locally.");
        if (!_control.Epoch.Equals(request.Epoch))
            throw new InvalidOperationException("stale_lease: local epoch changed.");
        var result = await _worker.RequestAsync("observe", DesktopWorkerHost.DefaultTimeout, ct);
        var pngB64 = result.GetProperty("png_b64").GetString()!;
        var png = Convert.FromBase64String(pngB64);
        var sha = result.GetProperty("sha256").GetString()!;
        var byteLength = result.GetProperty("byte_length").GetInt64();
        var artifactError = InputValidation.ValidateArtifact(sha, "image/png", byteLength);
        if (artifactError is not null)
            throw new InvalidOperationException($"invalid_artifact: {artifactError.Message}");
        if (png.Length != byteLength ||
            Convert.ToHexString(SHA256.HashData(png)).ToLowerInvariant() != sha)
            throw new InvalidOperationException("invalid_artifact: SHA-256 mismatch.");

        var capturedAt = DateTimeOffset.Parse(result.GetProperty("captured_at").GetString()!);
        var crop = result.GetProperty("crop_native");
        var virtualDesktop = result.GetProperty("virtual_desktop");
        var artifactId = Guid.NewGuid();
        var observation = new Observation
        {
            SchemaVersion = "1.0",
            ObservationId = Guid.NewGuid(),
            DeviceId = request.Request.DeviceId,
            LeaseId = request.Request.LeaseId,
            Epoch = request.Epoch,
            LayoutRevision = _control.Generations.Current.LayoutRevision,
            ForegroundGeneration = _control.Generations.Current.ForegroundGeneration,
            InputGeneration = _control.Generations.Current.InputGeneration,
            ForegroundIdentity = result.GetProperty("foreground_identity").GetString()!,
            CapturedAt = capturedAt,
            ExpiresAt = capturedAt.Add(ObservationConstraints.DefaultMaxAge),
            Artifact = new ObservationArtifact
            {
                ArtifactId = artifactId,
                Sha256 = sha,
                MimeType = "image/png",
                ByteLength = byteLength,
            },
            ImageWidth = result.GetProperty("image_width").GetInt32(),
            ImageHeight = result.GetProperty("image_height").GetInt32(),
            CropNative = new NativeRectangle(
                crop.GetProperty("left").GetInt32(), crop.GetProperty("top").GetInt32(),
                crop.GetProperty("width").GetInt32(), crop.GetProperty("height").GetInt32()),
            VirtualDesktop = new NativeRectangle(
                virtualDesktop.GetProperty("left").GetInt32(), virtualDesktop.GetProperty("top").GetInt32(),
                virtualDesktop.GetProperty("width").GetInt32(), virtualDesktop.GetProperty("height").GetInt32()),
            Uia = result.GetProperty("uia"),
        };
        lock (_observations)
        {
            _observations[request.Request.LeaseId] = observation;
            _artifacts[artifactId] = png;
            PruneArtifactsLocked();
        }
        return observation;
    }

    /// <summary>The PNG bytes for a published artifact id (bounded store).</summary>
    public byte[]? ArtifactBytes(Guid artifactId)
    {
        lock (_observations)
            return _artifacts.TryGetValue(artifactId, out var png) ? png : null;
    }

    private void PruneArtifactsLocked()
    {
        // Keep the newest 8 artifacts; the relay caches for 60 s.
        while (_artifacts.Count > 8)
        {
            var oldest = _artifacts.Keys.First();
            _artifacts.Remove(oldest);
        }
    }

    // ---- T16/T17: input with pre-checks ----

    public async Task<ActionResult> ExecuteAsync(
        ActionEnvelope request, AuthenticatedController controller, CancellationToken ct)
    {
        // Validation first: pre-acceptance rejections do not consume
        // the sequence and do not touch the journal.
        var timeError = InputValidation.ValidateTimes(request.IssuedAt, request.ExpiresAt, DateTimeOffset.UtcNow);
        if (timeError is not null)
            throw new InvalidOperationException($"{timeError.Code}: {timeError.Message}");
        var payloadError = request.Operation switch
        {
            OperationNames.Click => ValidateClickPayload(request.Payload),
            OperationNames.TypeText => ValidateTypePayload(request.Payload),
            OperationNames.Key => ValidateKeyPayload(request.Payload),
            _ => new InputValidation.ValidationError("unsupported_operation",
                $"Operation '{request.Operation}' is not in v1.0."),
        };
        if (payloadError is not null)
            throw new InvalidOperationException($"{payloadError.Code}: {payloadError.Message}");

        var binding = CommandJournal.BindingHash(
            request.CommandId.ToString(), request.DeviceId, request.LeaseId.ToString(),
            request.Epoch, request.Sequence, request.Operation,
            request.Payload.GetRawText(), controller.PrincipalId, controller.ControllerId);
        var (accepted, replay) = _journal.Accept(
            request.CommandId.ToString(), request.DeviceId, request.LeaseId.ToString(),
            request.Epoch, request.Sequence, request.Operation,
            binding, controller.PrincipalId, controller.ControllerId);
        if (!accepted)
        {
            return new ActionResult(request.CommandId, replay!.Status,
                replay.Status == "succeeded" ? "ok" : "replayed",
                DateTimeOffset.UtcNow, 0);
        }

        await _inputQueue.WaitAsync(ct);
        try
        {
            // Re-check local state immediately before the side effect.
            if (_control.Paused)
            {
                _journal.Finish(request.CommandId.ToString(), "cancelled", null);
                throw new InvalidOperationException("local_paused: the desktop is paused locally.");
            }
            if (_control.Epoch != request.Epoch)
            {
                _journal.Finish(request.CommandId.ToString(), "cancelled", null);
                throw new InvalidOperationException("stale_lease: local epoch changed.");
            }
            Observation? observation;
            lock (_observations)
                _observations.TryGetValue(request.LeaseId, out observation);
            if (observation is null || observation.ObservationId != request.ObservationId)
            {
                _journal.Finish(request.CommandId.ToString(), "failed", null);
                throw new InvalidOperationException("stale_observation: no fresh observation for this lease.");
            }
            if (!ObservationConstraints.IsFresh(observation.CapturedAt, DateTimeOffset.UtcNow) ||
                !_control.Generations.Current.Equals(new ObservationConstraints.Generations(
                    observation.LayoutRevision, observation.ForegroundGeneration, observation.InputGeneration)))
            {
                _journal.Finish(request.CommandId.ToString(), "failed", null);
                throw new InvalidOperationException("stale_observation: the desktop changed since the observation.");
            }

            _journal.MarkExecuting(request.CommandId.ToString());
            var steps = 0;
            try
            {
                steps = ExecuteInput(request, ct);
            }
            catch (Exception)
            {
                // A failure mid-input has unknown effects: uncertain,
                // never a silent success, never an automatic replay.
                _journal.Finish(request.CommandId.ToString(), "uncertain", null);
                throw;
            }
            if (steps < 0)
            {
                _journal.Finish(request.CommandId.ToString(), "uncertain", null);
                throw new InvalidOperationException("uncertain: SendInput injected fewer events than sent.");
            }
            _control.Generations.NoteInputExecuted();
            lock (_observations)
                _observations.Remove(request.LeaseId); // one accepted input consumes the observation
            _journal.Finish(request.CommandId.ToString(), "succeeded", "{\"ok\":true}");
            return new ActionResult(request.CommandId, "succeeded", "ok",
                DateTimeOffset.UtcNow, steps);
        }
        finally
        {
            _inputQueue.Release();
        }
    }

    private int ExecuteInput(ActionEnvelope request, CancellationToken ct)
    {
        switch (request.Operation)
        {
            case OperationNames.Click:
            {
                var x = request.Payload.GetProperty("x").GetInt32();
                var y = request.Payload.GetProperty("y").GetInt32();
                // Image pixels -> native virtual-desktop pixels, then click.
                Observation? observation;
                lock (_observations)
                    _observations.TryGetValue(request.LeaseId, out observation);
                var (nativeX, nativeY) = CoordinateMapper.ImageToNative(
                    x, y, observation!.ImageWidth, observation.ImageHeight,
                    observation.CropNative.Left, observation.CropNative.Top,
                    observation.CropNative.Width, observation.CropNative.Height);
                return Win32Desktop.ClickAtNative(nativeX, nativeY) ? 1 : -1;
            }
            case OperationNames.TypeText:
            {
                var text = request.Payload.GetProperty("text").GetString()!;
                // Type in bounded chunks so a stop can interrupt it.
                var injected = 0;
                foreach (var chunk in ChunkText(text, 200))
                {
                    ct.ThrowIfCancellationRequested();
                    injected += Win32Desktop.TypeUnicode(chunk, ct);
                }
                return injected == text.Length ? injected : -1;
            }
            case OperationNames.Key:
            {
                var keys = request.Payload.GetProperty("keys").EnumerateArray()
                    .Select(e => e.GetString()!).ToList();
                var ok = Win32Desktop.PressKeys(keys);
                if (ok)
                {
                    lock (_heldModifiers)
                    {
                        foreach (var key in keys)
                            if (key is "CTRL" or "ALT" or "SHIFT" or "WIN")
                                _heldModifiers.Add(Win32Desktop.VirtualKeyFor(key));
                    }
                }
                return ok ? 1 : -1;
            }
            default:
                throw new InvalidOperationException($"Unsupported operation: {request.Operation}");
        }
    }

    private static IEnumerable<string> ChunkText(string text, int size)
    {
        for (var i = 0; i < text.Length; i += size)
            yield return text[i..Math.Min(text.Length, i + size)];
    }

    private static InputValidation.ValidationError? ValidateClickPayload(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty("x", out var x) || !payload.TryGetProperty("y", out var y) ||
            x.ValueKind != JsonValueKind.Number || y.ValueKind != JsonValueKind.Number)
            return new InputValidation.ValidationError("invalid_payload", "click needs integer x and y.");
        return InputValidation.ValidateClick(x.GetInt32(), y.GetInt32());
    }

    private static InputValidation.ValidationError? ValidateTypePayload(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty("text", out var text) ||
            text.ValueKind != JsonValueKind.String)
            return new InputValidation.ValidationError("invalid_payload", "type_text needs a text string.");
        return InputValidation.ValidateTypeText(text.GetString());
    }

    private static InputValidation.ValidationError? ValidateKeyPayload(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty("keys", out var keys) ||
            keys.ValueKind != JsonValueKind.Array)
            return new InputValidation.ValidationError("invalid_payload", "key needs a keys array.");
        var list = keys.EnumerateArray()
            .Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() : null)
            .ToList();
        if (list.Any(k => k is null))
            return new InputValidation.ValidationError("invalid_payload", "keys must be strings.");
        return InputValidation.ValidateKeys(list.Select(k => k!).ToList());
    }

    // ---- T19: release only what this backend held ----

    public Task ReleaseOwnedInputsAsync(CancellationToken ct)
    {
        List<ushort> held;
        lock (_heldModifiers)
        {
            held = _heldModifiers.ToList();
            _heldModifiers.Clear();
        }
        Win32Desktop.ReleaseKeys(held);
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        await ReleaseOwnedInputsAsync(CancellationToken.None);
        _journal.Dispose();
        _inputQueue.Dispose();
    }
}
