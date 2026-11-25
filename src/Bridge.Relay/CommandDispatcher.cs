using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bridge.Contracts;

namespace Bridge.Relay;

/// <summary>
/// Command receipt returned to the caller (lab _stored shape).
/// </summary>
public sealed record CommandReceipt(
    string CommandId, string Status, bool Duplicate, JsonNode? Result);

/// <summary>
/// Lease handed to the caller (lab acquire_lease shape).
/// </summary>
public sealed record Lease(string DeviceId, string LeaseId, long Epoch, string ExpiresAt);

/// <summary>
/// C# port of contract_lab's ContractEngine (T08). Same state machine:
/// accepted → executing → succeeded | failed | uncertain | cancelled.
/// Journal row is written BEFORE any side effect; duplicates return the
/// stored receipt without a second effect; same id + different binding
/// is IDEMPOTENCY_CONFLICT; a restart marks unfinished commands
/// uncertain (see CommandJournal). Principal/controller are trusted
/// auth context, never JSON body fields.
/// </summary>
public sealed class CommandDispatcher
{
    private readonly CommandJournal _journal;
    private readonly IRelayBackend _backend;
    private readonly IClock _clock;
    private readonly object _gate = new();
    private readonly Dictionary<string, double> _leaseDeadlines = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _observationDeadlines = new(StringComparer.Ordinal);

    public CommandDispatcher(CommandJournal journal, IRelayBackend backend, IClock? clock = null)
    {
        _journal = journal;
        _backend = backend;
        _clock = clock ?? new SystemClock();
    }

    // ---- identity / lease / pause (privileged local ops mirror the lab) ----

    public void RegisterDevice(string deviceId, string ownerId, string controllerId)
    {
        if (!IsIdentifier(deviceId) || !IsIdentifier(ownerId) || !IsIdentifier(controllerId))
            throw new ContractError("INVALID_IDENTITY", "device/owner/controller ids must be 1..128 [A-Za-z0-9_.:-].");
        _journal.RegisterDevice(deviceId, ownerId, controllerId);
    }

    public Lease AcquireLease(string principal, string controller, string deviceId, int ttlSeconds = 60)
    {
        if (ttlSeconds < 1 || ttlSeconds > 300)
            throw new ContractError("INVALID_LEASE_TTL", "Lease TTL must be 1..300s.");
        lock (_gate)
        {
            var row = RequireDevice(principal, controller, deviceId);
            if (row.Paused) throw new ContractError("LOCAL_PAUSED", "Device is paused locally.");
            if (row.ActiveCommandId is not null)
                throw new ContractError("DEVICE_BUSY", "A command is already executing on this device.");
            if (row.LeaseId is not null && row.LeaseUntil is not null &&
                _clock.WallSeconds() < row.LeaseUntil.Value &&
                _clock.MonotonicSeconds() < _leaseDeadlines.GetValueOrDefault(row.LeaseId, double.NegativeInfinity))
                throw new ContractError("DEVICE_BUSY", "A live lease already exists for this device.");
            var leaseId = Guid.NewGuid().ToString("D");
            var epoch = row.Epoch + 1;
            var expires = _clock.WallSeconds() + ttlSeconds;
            _journal.UpdateDeviceLease(deviceId, leaseId, expires, epoch);
            _leaseDeadlines[leaseId] = _clock.MonotonicSeconds() + ttlSeconds;
            return new Lease(deviceId, leaseId, epoch, UtcString(expires));
        }
    }

    public void Pause(string deviceId) => _journal.SetPaused(deviceId, true);
    public void ResumeLocal(string deviceId) => _journal.SetPaused(deviceId, false);

    // ---- observation ----

    public (string ObservationId, CapturedSurface Surface, string ExpiresAt) Observe(
        string principal, string controller, Lease lease)
    {
        lock (_gate)
        {
            var row = RequireLease(principal, controller, lease.DeviceId, lease.LeaseId, lease.Epoch);
            if (row.ActiveCommandId is not null)
                throw new ContractError("DEVICE_BUSY", "A command is already executing on this device.");
        }
        // Native capture may block; never hold the lock across it.
        var surface = _backend.Capture(lease.DeviceId);
        ValidateSurface(surface);
        lock (_gate)
        {
            var row = RequireLease(principal, controller, lease.DeviceId, lease.LeaseId, lease.Epoch);
            if (row.ActiveCommandId is not null)
                throw new ContractError("DEVICE_BUSY", "A command is already executing on this device.");
            var observationId = Guid.NewGuid().ToString("D");
            var expires = Math.Min(_clock.WallSeconds() + 30, row.LeaseUntil ?? double.MaxValue);
            _journal.InsertObservation(new CommandJournal.ObservationRow(
                observationId, lease.DeviceId, lease.LeaseId, lease.Epoch,
                expires, SurfaceJson(surface), false));
            _observationDeadlines[observationId] =
                _clock.MonotonicSeconds() + Math.Min(30, Math.Max(0, expires - _clock.WallSeconds()));
            return (observationId, surface, UtcString(expires));
        }
    }

    // ---- command status read ----

    public CommandReceipt GetCommand(string principal, string controller, string deviceId, string commandId)
    {
        lock (_gate)
        {
            RequireDevice(principal, controller, deviceId);
            var row = _journal.GetCommandRow(commandId, deviceId)
                ?? throw new ContractError("NOT_FOUND", $"Unknown command: {commandId}.");
            if (row.OwnerId != principal || row.ControllerId != controller)
                throw new ContractError("NOT_FOUND", $"Unknown command: {commandId}.");
            return Stored(row, false);
        }
    }

    // ---- dispatch ----

    public CommandReceipt Dispatch(string principal, string controller, string envelopeJson)
    {
        // Validate + digest first (lab _validate_envelope). The digest is
        // computed over the canonical form, so key order does not matter.
        // Wall-clock rejection is DEFERRED until after the duplicate check:
        // contract_lab only checks TTL in _validate_envelope and enforces
        // expiry in the locked section, so a duplicate of an expired
        // command returns the stored receipt (test_duplicate_after_expiry).
        CommandEnvelope env;
        try
        {
            env = EnvelopeValidator.Validate(envelopeJson,
                DateTimeOffset.FromUnixTimeSeconds((long)_clock.WallSeconds()));
        }
        catch (EnvelopeException ex) when (ex.Code is "EXPIRED" or "CLOCK_SKEW")
        {
            env = EnvelopeValidator.Validate(envelopeJson, ExtractIssuedAt(envelopeJson, ex));
        }
        catch (EnvelopeException ex) { throw MapEnvelopeError(ex); }
        var digest = EnvelopeDigest.DigestEnvelope(envelopeJson);

        lock (_gate)
        {
            RequireDevice(principal, controller, env.DeviceId);
            var dup = FindDuplicate(principal, controller, env, digest);
            if (dup is not null) return dup;
            RequireLease(principal, controller, env.DeviceId, env.LeaseId, env.Epoch);
        }

        // Native state lookup outside the lock; rechecked below.
        var currentState = _backend.CurrentState(env.DeviceId);
        var commandDeadline = _clock.MonotonicSeconds()
            + Math.Max(0, env.ExpiresAt.ToUnixTimeMilliseconds() / 1000.0 - _clock.WallSeconds());

        CapturedSurface surface;
        Dictionary<string, object?> nativePayload;
        lock (_gate)
        {
            var row = RequireLease(principal, controller, env.DeviceId, env.LeaseId, env.Epoch);
            var dup = FindDuplicate(principal, controller, env, digest);
            if (dup is not null) return dup;
            if (row.ActiveCommandId is not null)
                throw new ContractError("DEVICE_BUSY", "A command is already executing on this device.");
            if (_clock.WallSeconds() >= env.ExpiresAt.ToUnixTimeMilliseconds() / 1000.0
                || env.IssuedAt.ToUnixTimeMilliseconds() / 1000.0 > _clock.WallSeconds() + 2)
                throw new ContractError("COMMAND_EXPIRED_OR_FUTURE", "Command is expired or too far in the future.");
            if (env.Sequence != row.NextSequence)
                throw new ContractError("SEQUENCE_MISMATCH",
                    $"Expected sequence {row.NextSequence}, got {env.Sequence}.");
            var observation = _journal.GetObservation(env.ObservationId);
            if (observation is null ||
                (observation.DeviceId, observation.LeaseId, observation.Epoch) !=
                (env.DeviceId, env.LeaseId, env.Epoch))
                throw new ContractError("STALE_OBSERVATION", "Observation does not match this command.");
            if (observation.Consumed || _clock.WallSeconds() >= observation.ExpiresAt ||
                _clock.MonotonicSeconds() >= _observationDeadlines.GetValueOrDefault(
                    env.ObservationId, double.NegativeInfinity))
                throw new ContractError("STALE_OBSERVATION", "Observation is consumed or expired.");
            surface = ParseSurface(observation.SurfaceJson);
            if (!currentState.Unlocked)
                throw new ContractError("DESKTOP_LOCKED", "Desktop is locked.");
            if (currentState != surface.State)
                throw new ContractError("STALE_OBSERVATION", "Desktop state changed since the observation.");

            nativePayload = BuildNativePayload(env, surface);

            // Journal BEFORE any side effect (single transaction).
            _journal.InsertExecuting(new CommandJournal.CommandRow(
                env.CommandId, env.DeviceId, principal, controller,
                env.LeaseId, env.Epoch, digest, "executing", "{}"), env.ObservationId);
        }

        bool ShouldAbort()
        {
            lock (_gate)
            {
                try { RequireLease(principal, controller, env.DeviceId, env.LeaseId, env.Epoch); }
                catch (ContractError) { return true; }
                return _clock.WallSeconds() >= env.ExpiresAt.ToUnixTimeMilliseconds() / 1000.0
                    || _clock.MonotonicSeconds() >= commandDeadline;
            }
        }

        // Execute MUST check should_abort immediately before EACH bounded OS input.
        string status;
        JsonNode result;
        try
        {
            if (ShouldAbort())
            {
                status = "cancelled";
                result = ResultNode("ABORTED_BEFORE_BACKEND");
            }
            else
            {
                var receipt = _backend.Execute(env.DeviceId, env.Operation, nativePayload, ShouldAbort);
                var encoded = JsonSerializer.Serialize(receipt);
                if (encoded.Length > 8192)
                    throw new InvalidOperationException("Backend receipt is too large");
                if (ShouldAbort())
                {
                    status = "uncertain";
                    result = ResultNode("STOP_OR_EXPIRY_DURING_EXECUTION");
                }
                else
                {
                    status = "succeeded";
                    result = new JsonObject
                    {
                        ["code"] = "BACKEND_RETURNED",
                        ["receipt"] = JsonNode.Parse(encoded),
                    };
                }
            }
        }
        catch (NotImplementedException)
        {
            status = "failed";
            result = ResultNode("BACKEND_NOT_IMPLEMENTED");
        }
        catch
        {
            // Includes timeouts: the OS effect may already have occurred.
            status = "uncertain";
            result = ResultNode("BACKEND_OUTCOME_UNKNOWN");
        }

        lock (_gate)
        {
            _journal.Finish(env.CommandId, env.DeviceId, status, result.ToJsonString());
            return GetCommand(principal, controller, env.DeviceId, env.CommandId);
        }
    }

    // ---- internals ----

    private CommandReceipt? FindDuplicate(
        string principal, string controller, CommandEnvelope env, string digest)
    {
        var row = _journal.GetCommandRowById(env.CommandId);
        if (row is null) return null;
        if ((row.DeviceId, row.OwnerId, row.ControllerId, row.LeaseId, row.Epoch, row.EnvelopeHash) !=
            (env.DeviceId, principal, controller, env.LeaseId, env.Epoch, digest))
            throw new ContractError("IDEMPOTENCY_CONFLICT",
                "command_id already used with a different binding.");
        return Stored(row, true);
    }

    private static CommandReceipt Stored(CommandJournal.CommandRow row, bool duplicate) =>
        new(row.CommandId, row.Status, duplicate, JsonNode.Parse(row.ResultJson));

    private CommandJournal.DeviceRow RequireDevice(string principal, string controller, string deviceId)
    {
        var row = _journal.GetDevice(deviceId);
        if (row is null || row.OwnerId != principal || row.ControllerId != controller)
            throw new ContractError("FORBIDDEN", "Device is not owned by this principal/controller.");
        return row;
    }

    private CommandJournal.DeviceRow RequireLease(
        string principal, string controller, string deviceId, string leaseId, long epoch)
    {
        var row = RequireDevice(principal, controller, deviceId);
        if (row.Paused) throw new ContractError("LOCAL_PAUSED", "Device is paused locally.");
        if (row.LeaseId != leaseId || row.Epoch != epoch)
            throw new ContractError("STALE_LEASE", "Lease id or epoch does not match.");
        if (row.LeaseUntil is null || _clock.WallSeconds() >= row.LeaseUntil.Value ||
            _clock.MonotonicSeconds() >= _leaseDeadlines.GetValueOrDefault(leaseId, double.NegativeInfinity))
            throw new ContractError("LEASE_EXPIRED", "Lease has expired.");
        return row;
    }

    private static Dictionary<string, object?> BuildNativePayload(
        CommandEnvelope env, CapturedSurface surface)
    {
        if (env.Payload is ClickPayload click)
        {
            // Production v1 contract uses floor; never add an implicit half pixel.
            var (nx, ny) = CoordinateMapper.Map(
                click.X, click.Y, surface.ImageWidth, surface.ImageHeight,
                surface.CropX, surface.CropY, surface.CropWidth, surface.CropHeight);
            if (!(surface.CropX <= nx && nx < surface.CropX + surface.CropWidth &&
                  surface.CropY <= ny && ny < surface.CropY + surface.CropHeight &&
                  surface.VirtualX <= nx && nx < surface.VirtualX + surface.VirtualWidth &&
                  surface.VirtualY <= ny && ny < surface.VirtualY + surface.VirtualHeight))
                throw new ContractError("COORDINATE_OUT_OF_RANGE", "Mapped point is outside the crop/virtual desktop.");
            return new Dictionary<string, object?>
            {
                ["native_x"] = nx,
                ["native_y"] = ny,
                ["normalized_x"] = (nx - surface.VirtualX) * 65535 / Math.Max(1, surface.VirtualWidth - 1),
                ["normalized_y"] = (ny - surface.VirtualY) * 65535 / Math.Max(1, surface.VirtualHeight - 1),
                ["virtual_desktop"] = true,
            };
        }
        if (env.Payload is TypeTextPayload text)
            return new Dictionary<string, object?> { ["text"] = text.Text };
        if (env.Payload is KeyPayload keys)
            return new Dictionary<string, object?> { ["keys"] = keys.Keys.ToList() };
        throw new ContractError("UNSUPPORTED_OPERATION", $"Unsupported operation: {env.Operation}.");
    }

    private static void ValidateSurface(CapturedSurface surface)
    {
        var dims = new[]
        {
            surface.ImageWidth, surface.ImageHeight, surface.CropWidth,
            surface.CropHeight, surface.VirtualWidth, surface.VirtualHeight,
        };
        if (dims.Any(d => d < 1 || d > 65536))
            throw new ContractError("INVALID_CAPTURE", "Surface dimensions must be 1..65536.");
        if (!(surface.VirtualX <= surface.CropX && surface.VirtualY <= surface.CropY &&
              surface.CropX + surface.CropWidth <= surface.VirtualX + surface.VirtualWidth &&
              surface.CropY + surface.CropHeight <= surface.VirtualY + surface.VirtualHeight))
            throw new ContractError("INVALID_CAPTURE", "Crop must sit inside the virtual desktop.");
        if (!surface.State.Unlocked)
            throw new ContractError("DESKTOP_LOCKED", "Desktop is locked.");
        var gens = new[]
        {
            surface.State.LayoutRevision, surface.State.ForegroundGeneration,
            surface.State.InputGeneration,
        };
        if (gens.Any(g => g < 0) || !IsIdentifier(surface.State.ForegroundToken))
            throw new ContractError("INVALID_CAPTURE", "Surface state generations/token are invalid.");
    }

    private static string SurfaceJson(CapturedSurface s)
    {
        // Sorted keys, like the lab's json.dumps(asdict(surface), sort_keys=True).
        var state = new SortedDictionary<string, object?>
        {
            ["foreground_generation"] = s.State.ForegroundGeneration,
            ["foreground_token"] = s.State.ForegroundToken,
            ["input_generation"] = s.State.InputGeneration,
            ["layout_revision"] = s.State.LayoutRevision,
            ["unlocked"] = s.State.Unlocked,
        };
        var doc = new SortedDictionary<string, object?>
        {
            ["capture_kind"] = s.CaptureKind,
            ["crop_height"] = s.CropHeight,
            ["crop_width"] = s.CropWidth,
            ["crop_x"] = s.CropX,
            ["crop_y"] = s.CropY,
            ["image_height"] = s.ImageHeight,
            ["image_width"] = s.ImageWidth,
            ["state"] = state,
            ["virtual_height"] = s.VirtualHeight,
            ["virtual_width"] = s.VirtualWidth,
            ["virtual_x"] = s.VirtualX,
            ["virtual_y"] = s.VirtualY,
        };
        return JsonSerializer.Serialize(doc);
    }

    private static CapturedSurface ParseSurface(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var state = root.GetProperty("state");
        return new CapturedSurface(
            new DesktopState(
                LayoutRevision: state.GetProperty("layout_revision").GetInt64(),
                ForegroundToken: state.GetProperty("foreground_token").GetString() ?? "",
                InputGeneration: state.GetProperty("input_generation").GetInt64(),
                Unlocked: state.GetProperty("unlocked").GetBoolean(),
                ForegroundGeneration: state.GetProperty("foreground_generation").GetInt64()),
            root.GetProperty("image_width").GetInt64(),
            root.GetProperty("image_height").GetInt64(),
            root.GetProperty("crop_x").GetInt64(),
            root.GetProperty("crop_y").GetInt64(),
            root.GetProperty("crop_width").GetInt64(),
            root.GetProperty("crop_height").GetInt64(),
            root.GetProperty("virtual_x").GetInt64(),
            root.GetProperty("virtual_y").GetInt64(),
            root.GetProperty("virtual_width").GetInt64(),
            root.GetProperty("virtual_height").GetInt64(),
            root.TryGetProperty("capture_kind", out var k) ? k.GetString() ?? "native" : "native");
    }

    private static DateTimeOffset ExtractIssuedAt(string json, EnvelopeException original)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var value = doc.RootElement.GetProperty("issued_at").GetString()!;
            return DateTimeOffset.ParseExact(value,
                new[] { "yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFF'Z'" },
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
        }
        catch (Exception) { throw MapEnvelopeError(original); }
    }

    private static JsonObject ResultNode(string code) => new() { ["code"] = code };

    public static string UtcString(double unixSeconds) =>
        DateTimeOffset.FromUnixTimeSeconds((long)unixSeconds)
            .ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    internal static bool IsIdentifier(string value) =>
        value.Length is >= 1 and <= 128 &&
        value.All(c => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') ||
                       (c >= '0' && c <= '9') || c is '_' or '.' or ':' or '-');

    /// <summary>Validator codes (Bridge.Contracts) → wire codes (contract_lab). </summary>
    private static ContractError MapEnvelopeError(EnvelopeException ex) => ex.Code switch
    {
        "BAD_TTL" => new ContractError("INVALID_COMMAND_TTL", ex.Message),
        "EXPIRED" or "CLOCK_SKEW" => new ContractError("COMMAND_EXPIRED_OR_FUTURE", ex.Message),
        "BAD_COORDINATE" or "BAD_TEXT" or "BAD_KEY" => new ContractError("INVALID_PAYLOAD", ex.Message),
        "UNKNOWN_OPERATION" => new ContractError("UNSUPPORTED_OPERATION", ex.Message),
        _ => new ContractError("INVALID_ENVELOPE", ex.Message),
    };
}
