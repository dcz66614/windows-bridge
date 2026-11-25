using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Bridge.DeviceIdentity;

namespace Bridge.DeviceChannel;

/// <summary>
/// The device side of the WSS channel (T11). The Windows agent runs
/// this: it dials the fixed WSS address with its device credential,
/// answers the server challenge by signing it with the device key,
/// heartbeats every 5 s, and executes commands the server sends.
/// Reconnect uses exponential backoff with jitter, capped at 30 s.
/// Close codes 4401/4403 are non-retryable: bad credentials or a
/// revoked grant must NOT be masked by infinite retries.
/// </summary>
public sealed class DeviceChannelClient
{
    public int? Epoch { get; private set; }
    public string? LastError { get; private set; }
    public bool Connected { get; private set; }

    /// <summary>
    /// Local device state (T12). The agent sets these from the tray /
    /// lock-screen watcher. The client refuses to execute input while
    /// paused or locked — even if the server did not know yet — and
    /// reports the state to the server so it stops dispatching.
    /// Only a local resume clears a pause.
    /// </summary>
    public bool LocalPaused { get; set; }
    public bool LocalLocked { get; set; }

    public event Action<int>? OnConnected;
    public event Action<string>? OnDisconnected;

    /// <summary>Report the local paused/locked state to the server.</summary>
    public async Task ReportLocalStateAsync(CancellationToken ct)
    {
        if (_socket is null || _socket.State != WebSocketState.Open)
            return;
        await ChannelFraming.SendAsync(_socket,
            ChannelEnvelope.Create("lease_update", new
            {
                paused = LocalPaused,
                locked = LocalLocked,
            }), ct);
    }

    private ClientWebSocket? _socket;

    private static readonly Random Jitter = new();

    /// <summary>
    /// Runs the channel until <paramref name="ct"/> is cancelled or a
    /// non-retryable close (4401/4403) arrives. Returns normally on
    /// cancellation; throws on a non-retryable failure.
    /// </summary>
    public async Task RunAsync(
        Uri wsUri,
        string accessToken,
        string deviceId,
        DeviceKeyPair keyPair,
        Func<JsonElement, CancellationToken, Task<JsonElement>> onCommand,
        CancellationToken ct)
    {
        var attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(wsUri, accessToken, deviceId, keyPair, onCommand, ct);
                // Clean exit (server closed normally): stop, don't spin.
                return;
            }
            catch (NonRetryableException ex)
            {
                LastError = ex.Message;
                OnDisconnected?.Invoke(ex.Message);
                throw;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                OnDisconnected?.Invoke(ex.Message);
                attempt++;
                var delay = Backoff(attempt);
                await Task.Delay(delay, ct);
            }
        }
    }

    public static TimeSpan Backoff(int attempt)
    {
        var seconds = Math.Min(30, Math.Pow(2, Math.Min(attempt, 5)));
        var jitter = Jitter.NextDouble() * 0.5 * seconds;
        return TimeSpan.FromSeconds(seconds + jitter);
    }

    private async Task RunOnceAsync(
        Uri wsUri,
        string accessToken,
        string deviceId,
        DeviceKeyPair keyPair,
        Func<JsonElement, CancellationToken, Task<JsonElement>> onCommand,
        CancellationToken ct)
    {
        using var socket = new ClientWebSocket();
        _socket = socket;
        socket.Options.SetRequestHeader("Authorization", "Bearer " + accessToken);
        await socket.ConnectAsync(wsUri, ct);

        // The server sends a challenge first; answer it with a hello
        // signed over (nonce + ":" + device_id).
        var challengeJson = await ChannelFraming.ReadMessageAsync(socket, ct)
            ?? throw new InvalidOperationException("Connection closed before challenge.");
        var challenge = ChannelEnvelope.TryParse(challengeJson)
            ?? throw new InvalidOperationException("Bad challenge frame.");
        if (challenge.Type != "challenge")
            throw new InvalidOperationException($"Expected challenge, got {challenge.Type}.");
        var nonce = challenge.Body.GetProperty("nonce_b64").GetString()!;
        var material = Encoding.UTF8.GetBytes(nonce + ":" + deviceId);
        var signature = keyPair.Sign(material);
        await ChannelFraming.SendAsync(socket, ChannelEnvelope.Create("hello", new
        {
            device_id = deviceId,
            public_key_fingerprint = keyPair.Fingerprint,
            signature_b64 = Convert.ToBase64String(signature),
        }, Guid.NewGuid().ToString("N")), ct);

        var ackJson = await ChannelFraming.ReadMessageAsync(socket, ct)
            ?? throw new InvalidOperationException("Connection closed before hello_ack.");
        var ack = ChannelEnvelope.TryParse(ackJson)
            ?? throw new InvalidOperationException("Bad hello_ack frame.");
        if (ack.Type == "error")
        {
            var code = ack.Body.GetProperty("code").GetString();
            var message = ack.Body.TryGetProperty("message", out var m) ? m.GetString() : code;
            if (code is "unauthorized" or "revoked")
                throw new NonRetryableException($"{code}: {message}");
            throw new InvalidOperationException(message ?? code ?? "hello failed");
        }
        if (ack.Type != "hello_ack")
            throw new InvalidOperationException($"Expected hello_ack, got {ack.Type}.");
        Epoch = ack.Body.GetProperty("epoch").GetInt32();
        Connected = true;
        OnConnected?.Invoke(Epoch.Value);

        var lastAck = DateTimeOffset.UtcNow;
        using var heartbeatTimer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        var heartbeatTask = HeartbeatLoopAsync(socket, heartbeatTimer, ct);
        try
        {
            while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                var json = await ChannelFraming.ReadMessageAsync(socket, ct);
                if (json is null)
                    break;
                var envelope = ChannelEnvelope.TryParse(json);
                if (envelope is null)
                    continue;
                switch (envelope.Type)
                {
                    case "heartbeat_ack":
                        lastAck = DateTimeOffset.UtcNow;
                        break;
                    case "command":
                    {
                        // Agent-side check (T12): never execute while
                        // locally paused or locked, even if the server
                        // dispatched before learning the state.
                        JsonElement result;
                        if (LocalPaused || LocalLocked)
                        {
                            result = JsonSerializer.SerializeToElement(new
                            {
                                status = "failed",
                                error = LocalPaused ? "local_paused" : "local_locked",
                            });
                        }
                        else
                        {
                            result = await onCommand(envelope.Body, ct);
                        }
                        await ChannelFraming.SendAsync(socket,
                            ChannelEnvelope.Create("command_result", result, envelope.MessageId), ct);
                        break;
                    }
                    case "revoke":
                        throw new NonRetryableException("Device revoked by the server.");
                    case "error":
                        throw new InvalidOperationException(
                            envelope.Body.TryGetProperty("message", out var m)
                                ? m.GetString() : "server error");
                    default:
                        // Unknown types are ignored (never executed).
                        break;
                }
                if (DateTimeOffset.UtcNow - lastAck > TimeSpan.FromSeconds(15))
                    throw new InvalidOperationException("Heartbeat ack timeout.");
            }
        }
        finally
        {
            Connected = false;
            _socket = null;
            await heartbeatTask;
        }
    }

    private static async Task HeartbeatLoopAsync(
        ClientWebSocket socket, PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                if (socket.State != WebSocketState.Open)
                    return;
                await ChannelFraming.SendAsync(socket,
                    ChannelEnvelope.Create("heartbeat", null, Guid.NewGuid().ToString("N")), ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (WebSocketException)
        {
        }
    }

    public sealed class NonRetryableException : Exception
    {
        public NonRetryableException(string message) : base(message)
        {
        }
    }
}
