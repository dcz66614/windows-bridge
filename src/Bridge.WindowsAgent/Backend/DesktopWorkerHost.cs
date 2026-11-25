using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace Bridge.WindowsAgent.Backend;

/// <summary>
/// Host for the isolated desktop worker process (T14/T18).
/// Starts Bridge.DesktopWorker.exe next to the agent, talks JSON
/// lines over a current-user-only named pipe, and restarts the
/// worker on crash or per-request timeout. At most 3 restarts in
/// a rolling minute; beyond that the backend reports degraded
/// until the user restarts the agent.
/// </summary>
public sealed class DesktopWorkerHost : IAsyncDisposable
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    private readonly string _workerPath;
    private readonly string _pipeName;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Queue<DateTimeOffset> _restarts = new();
    private Process? _process;
    private NamedPipeClientStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private bool _degraded;

    public DesktopWorkerHost()
    {
        _workerPath = Path.Combine(AppContext.BaseDirectory, "Bridge.DesktopWorker.exe");
        _pipeName = "BridgeDesktopWorker-" + Environment.UserName;
    }

    public bool Degraded => _degraded;

    public async Task<JsonElement> RequestAsync(
        string operation, TimeSpan timeout, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return await RequestLockedAsync(operation, timeout, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<JsonElement> RequestLockedAsync(
        string operation, TimeSpan timeout, CancellationToken ct)
    {
        if (_degraded)
            throw new InvalidOperationException(
                "Desktop worker is degraded after repeated crashes. Restart the agent.");
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        var token = timeoutCts.Token;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                await EnsureConnectedAsync(token);
                var id = Guid.NewGuid().ToString("N");
                await _writer!.WriteLineAsync(
                    JsonSerializer.Serialize(new { id, operation }));
                var line = await _reader!.ReadLineAsync(token);
                if (line is null)
                    throw new IOException("Worker closed the pipe.");
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.GetProperty("status").GetString() != "ok")
                    throw new InvalidOperationException(
                        root.TryGetProperty("error", out var e) ? e.GetString() : "Worker error.");
                return root.GetProperty("result").Clone();
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException)
            {
                // Crash or hang: kill, restart once, retry once.
                await KillWorkerAsync();
                if (attempt == 0)
                    continue;
                throw new TimeoutException("Desktop worker did not answer.", ex);
            }
        }
        throw new TimeoutException("Desktop worker did not answer.");
    }

    private async Task EnsureConnectedAsync(CancellationToken ct)
    {
        if (_pipe is not null && _pipe.IsConnected)
            return;
        await StartWorkerAsync(ct);
        _pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await _pipe.ConnectAsync(ct);
        _reader = new StreamReader(_pipe, Encoding.UTF8, leaveOpen: true);
        _writer = new StreamWriter(_pipe, Encoding.UTF8, leaveOpen: true)
        {
            NewLine = "\n",
            AutoFlush = true,
        };
    }

    private async Task StartWorkerAsync(CancellationToken ct)
    {
        if (!File.Exists(_workerPath))
            throw new FileNotFoundException(
                "Bridge.DesktopWorker.exe is missing next to the agent.", _workerPath);
        var now = DateTimeOffset.UtcNow;
        while (_restarts.Count > 0 && now - _restarts.Peek() > TimeSpan.FromMinutes(1))
            _restarts.Dequeue();
        if (_restarts.Count >= 3)
        {
            _degraded = true;
            throw new InvalidOperationException(
                "Desktop worker crashed 3 times in a minute; degraded until restart.");
        }
        _restarts.Enqueue(now);
        _process = Process.Start(new ProcessStartInfo
        {
            FileName = _workerPath,
            Arguments = _pipeName,
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        if (_process is null)
            throw new InvalidOperationException("Failed to start the desktop worker.");
        await Task.Delay(200, ct); // let the pipe server come up
    }

    private async Task KillWorkerAsync()
    {
        try { _reader?.Dispose(); } catch { }
        try { _writer?.Dispose(); } catch { }
        try { _pipe?.Dispose(); } catch { }
        _reader = null;
        _writer = null;
        _pipe = null;
        if (_process is not null)
        {
            try
            {
                if (!_process.HasExited)
                    _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync(CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch { }
            _process.Dispose();
            _process = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await KillWorkerAsync();
        _gate.Dispose();
    }
}
