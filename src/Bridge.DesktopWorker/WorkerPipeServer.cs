using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Bridge.Win32;

namespace Bridge.DesktopWorker;

/// <summary>
/// Named-pipe server for the worker process. One client (the tray)
/// at a time; the pipe allows only the current Windows user.
/// Requests and responses are single JSON lines.
/// </summary>
public sealed class WorkerPipeServer : IDisposable
{
    private readonly string _pipeName;
    private readonly Thread _mtaThread;
    private readonly BlockingCollection<(Func<object?> Work, TaskCompletionSource<object?> Done)> _queue = new();
    private bool _disposed;

    public WorkerPipeServer(string pipeName)
    {
        _pipeName = pipeName;
        _mtaThread = new Thread(RunMtaLoop)
        {
            Name = "DesktopWorker-MTA",
            IsBackground = true,
        };
        _mtaThread.SetApartmentState(ApartmentState.MTA);
        _mtaThread.Start();
    }

    private void RunMtaLoop()
    {
        foreach (var (work, done) in _queue.GetConsumingEnumerable())
        {
            try
            {
                done.SetResult(work());
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        }
    }

    private Task<T> RunOnMtaAsync<T>(Func<T> work)
    {
        var done = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Add((() => work(), done));
        return done.Task.ContinueWith(t => (T)t.Result!,
            TaskContinuationOptions.OnlyOnRanToCompletion);
    }

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && !_disposed)
        {
            var security = new PipeSecurity();
            var user = WindowsIdentity.GetCurrent().User;
            if (user is not null)
                security.AddAccessRule(new PipeAccessRule(user,
                    PipeAccessRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new PipeAccessRule(
                new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                PipeAccessRights.FullControl, AccessControlType.Deny));

            await using var pipe = NamedPipeServerStreamAcl.Create(
                _pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous, 0, 0, security);
            await pipe.WaitForConnectionAsync(ct);
            await ServeClientAsync(pipe, ct);
        }
    }

    private async Task ServeClientAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
        using var writer = new StreamWriter(pipe, Encoding.UTF8, leaveOpen: true)
        {
            NewLine = "\n",
            AutoFlush = true,
        };
        while (!ct.IsCancellationRequested && pipe.IsConnected)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null)
                break;
            var response = await HandleAsync(line, ct);
            await writer.WriteLineAsync(response);
        }
    }

    private async Task<string> HandleAsync(string line, CancellationToken ct)
    {
        string? id = null;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            id = root.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
            var operation = root.GetProperty("operation").GetString();
            var result = operation switch
            {
                "ping" => JsonSerializer.SerializeToElement(new { ok = true }),
                "probe" => await RunOnMtaAsync(Probe),
                "observe" => await RunOnMtaAsync(CaptureObservation),
                _ => throw new InvalidOperationException($"Unknown operation: {operation}"),
            };
            return JsonSerializer.Serialize(new { id, status = "ok", result });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                id,
                status = "error",
                error = ex.GetType().Name + ": " + ex.Message,
            });
        }
    }

    private static JsonElement Probe()
    {
        var (left, top, width, height) = Win32Desktop.VirtualDesktop();
        var monitors = Win32Desktop.EnumerateMonitors();
        var (hwnd, pid, title) = Win32Desktop.ForegroundWindow();
        return JsonSerializer.SerializeToElement(new
        {
            virtual_desktop = new { left, top, width, height },
            monitor_count = monitors.Count,
            monitors = monitors.Select(m => new
            {
                device_name = m.DeviceName,
                left = m.Left,
                top = m.Top,
                width = m.Width,
                height = m.Height,
                is_primary = m.IsPrimary,
                dpi_x = m.DpiX,
                dpi_y = m.DpiY,
            }),
            interactive_session = Win32Desktop.IsInteractiveSession(),
            desktop_unlocked = Win32Desktop.IsUnlocked(),
            foreground_window = new { hwnd = hwnd.ToInt64(), process_id = pid, title },
        });
    }

    private static JsonElement CaptureObservation()
    {
        var (png, imageWidth, imageHeight) = Win32Desktop.CaptureVirtualDesktopPng();
        var (left, top, width, height) = Win32Desktop.VirtualDesktop();
        var (hwnd, pid, _) = Win32Desktop.ForegroundWindow();
        var identity = ForegroundIdentity(hwnd, pid);
        var uia = UiaSnapshot.Capture();
        var sha = Convert.ToHexString(SHA256.HashData(png)).ToLowerInvariant();
        return JsonSerializer.SerializeToElement(new
        {
            captured_at = DateTimeOffset.UtcNow.ToString("O"),
            image_width = imageWidth,
            image_height = imageHeight,
            crop_native = new { left, top, width, height },
            virtual_desktop = new { left, top, width, height },
            png_b64 = Convert.ToBase64String(png),
            sha256 = sha,
            byte_length = png.Length,
            foreground_identity = identity,
            uia,
        });
    }

    private static string ForegroundIdentity(IntPtr hwnd, uint pid)
    {
        var start = string.Empty;
        try
        {
            if (pid != 0)
                start = Process.GetProcessById((int)pid).StartTime.ToUniversalTime().ToString("O");
        }
        catch
        {
            // The process may have exited between the calls.
        }
        var material = Encoding.UTF8.GetBytes($"{hwnd.ToInt64()}:{pid}:{start}");
        return Convert.ToHexString(SHA256.HashData(material)).ToLowerInvariant();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _queue.CompleteAdding();
    }
}
