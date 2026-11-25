using System.Collections.Concurrent;
using System.Text.Json;
using Bridge.DeviceChannel;

namespace Bridge.RelayService.DeviceChannel;

/// <summary>
/// Routes control commands to a live device connection and awaits the
/// result (T11). Commands are never queued while the device is
/// offline — they are rejected immediately. Each dispatch has a TTL;
/// an unanswered command expires as "uncertain" (the device may or
/// may not have executed it; the outcome is unknown, never claimed
/// as success).
/// </summary>
public sealed class CommandDispatcher
{
    public const int MaxPendingPerDevice = 32;
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromSeconds(30);

    public sealed record CommandOutcome(
        bool Delivered,
        string? Error,
        JsonElement? Result);

    private sealed class Pending
    {
        public required TaskCompletionSource<CommandOutcome> Completion { get; init; }
        public required DateTimeOffset ExpiresAt { get; init; }
    }

    private readonly DeviceConnectionRegistry _registry;
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, Pending>> _pending = new();

    public CommandDispatcher(DeviceConnectionRegistry registry) => _registry = registry;

    public async Task<CommandOutcome> DispatchAsync(
        string deviceId,
        JsonElement commandBody,
        TimeSpan? ttl = null,
        CancellationToken ct = default)
    {
        var connection = _registry.Get(deviceId);
        if (connection is null || connection.Socket.State != System.Net.WebSockets.WebSocketState.Open)
            return new CommandOutcome(false, "device_offline", null);

        var perDevice = _pending.GetOrAdd(deviceId, _ => new ConcurrentDictionary<string, Pending>());
        if (perDevice.Count >= MaxPendingPerDevice)
            return new CommandOutcome(false, "too_many_pending", null);

        var messageId = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<CommandOutcome>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var effectiveTtl = ttl ?? DefaultTtl;
        var pending = new Pending
        {
            Completion = completion,
            ExpiresAt = DateTimeOffset.UtcNow + effectiveTtl,
        };
        perDevice[messageId] = pending;

        try
        {
            await ChannelFraming.SendAsync(connection.Socket,
                ChannelEnvelope.Create("command", commandBody, messageId), ct);
        }
        catch
        {
            perDevice.TryRemove(messageId, out _);
            return new CommandOutcome(false, "device_offline", null);
        }

        using var timeout = new CancellationTokenSource(effectiveTtl);
        await using var registration = timeout.Token.Register(() =>
            completion.TrySetResult(new CommandOutcome(true, "command_timeout", null)));
        return await completion.Task;
    }

    /// <summary>Called by the WS endpoint when a command_result arrives.</summary>
    public bool TryComplete(string deviceId, string messageId, JsonElement result)
    {
        if (!_pending.TryGetValue(deviceId, out var perDevice))
            return false;
        if (!perDevice.TryRemove(messageId, out var pending))
            return false;
        return pending.Completion.TrySetResult(
            new CommandOutcome(true, null, result));
    }

    /// <summary>Fail every pending command for a device (disconnect).</summary>
    public void FailAll(string deviceId, string error)
    {
        if (!_pending.TryRemove(deviceId, out var perDevice))
            return;
        foreach (var pending in perDevice.Values)
            pending.Completion.TrySetResult(new CommandOutcome(false, error, null));
    }

    /// <summary>Pending commands whose TTL has passed (sweeper).</summary>
    public void ExpireStale()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (deviceId, perDevice) in _pending)
        {
            foreach (var (messageId, pending) in perDevice)
            {
                if (pending.ExpiresAt <= now && perDevice.TryRemove(messageId, out _))
                    pending.Completion.TrySetResult(
                        new CommandOutcome(true, "command_timeout", null));
            }
        }
    }
}
