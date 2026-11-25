using System.Collections.Concurrent;
using System.Net.WebSockets;

namespace Bridge.RelayService.DeviceChannel;

/// <summary>
/// The live device connections (T11). In-memory by design (docs/04
/// §3): a service restart drops all connections and leases. One
/// device_id has at most one live connection — a new hello evicts
/// the old one immediately, never a silent takeover.
/// </summary>
public sealed class DeviceConnectionRegistry
{
    public sealed record Connection
    {
        public required WebSocket Socket { get; init; }
        public required string DeviceId { get; init; }
        public required string OwnerId { get; init; }
        public required string Capabilities { get; init; }
        public required int Epoch { get; init; }
        public DateTimeOffset ConnectedAt { get; init; } = DateTimeOffset.UtcNow;
        public DateTimeOffset LastSeen { get; set; } = DateTimeOffset.UtcNow;

        /// <summary>
        /// Local device state reported by the agent (T12): paused or
        /// locked. The device is the authority; the server trusts its
        /// report. A pause is latched — only a local resume clears it.
        /// </summary>
        public bool Paused { get; set; }
        public bool Locked { get; set; }
        public DateTimeOffset LocalStateChangedAt { get; set; } = DateTimeOffset.UtcNow;
    }

    private readonly ConcurrentDictionary<string, Connection> _byDevice = new();
    private readonly ConcurrentDictionary<string, int> _epochs = new();

    /// <summary>
    /// Registers a new connection, evicting any live one for the same
    /// device_id. Returns the stored connection (with its epoch,
    /// monotonic per device while the service runs).
    /// </summary>
    public async Task<Connection> RegisterAsync(Connection connection, CancellationToken ct = default)
    {
        var epoch = _epochs.AddOrUpdate(connection.DeviceId, 1, (_, prev) => prev + 1);
        var registered = connection with { Epoch = epoch };
        if (_byDevice.TryGetValue(connection.DeviceId, out var old) &&
            !ReferenceEquals(old, registered))
        {
            _byDevice[connection.DeviceId] = registered;
            await CloseAsync(old.Socket, WebSocketCloseStatus.NormalClosure, "evicted");
        }
        else
        {
            _byDevice[connection.DeviceId] = registered;
        }
        return registered;
    }

    public Connection? Get(string deviceId) =>
        _byDevice.TryGetValue(deviceId, out var c) ? c : null;

    /// <summary>Update the device's reported local state (T12).</summary>
    public bool UpdateLocalState(string deviceId, bool paused, bool locked)
    {
        if (!_byDevice.TryGetValue(deviceId, out var connection))
            return false;
        connection.Paused = paused;
        connection.Locked = locked;
        connection.LocalStateChangedAt = DateTimeOffset.UtcNow;
        return true;
    }

    public void Unregister(string deviceId, Connection connection)
    {
        if (_byDevice.TryGetValue(deviceId, out var current) &&
            ReferenceEquals(current, connection))
            _byDevice.TryRemove(deviceId, out _);
    }

    /// <summary>Evict a live connection (revocation): send a revoke
    /// frame, then close. Returns false when nothing was live.</summary>
    public async Task<bool> EvictAsync(string deviceId, string reason, CancellationToken ct = default)
    {
        if (!_byDevice.TryRemove(deviceId, out var connection))
            return false;
        try
        {
            await Bridge.DeviceChannel.ChannelFraming.SendAsync(connection.Socket,
                Bridge.DeviceChannel.ChannelEnvelope.Create("revoke", new { reason }),
                ct);
        }
        catch
        {
            // The socket may already be dead; the close below still runs.
        }
        await CloseAsync(connection.Socket, (WebSocketCloseStatus)4403, reason);
        return true;
    }

    /// <summary>Connections with no heartbeat for longer than the
    /// given age, for the sweeper to close.</summary>
    public IEnumerable<Connection> StaleConnections(TimeSpan maxAge)
    {
        var cutoff = DateTimeOffset.UtcNow - maxAge;
        return _byDevice.Values.Where(c => c.LastSeen < cutoff).ToList();
    }

    public static async Task CloseAsync(WebSocket socket, WebSocketCloseStatus status, string reason)
    {
        try
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await socket.CloseAsync(status, reason, CancellationToken.None);
        }
        catch
        {
            // Already gone.
        }
    }
}
