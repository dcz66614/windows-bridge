namespace Bridge.RelayService.DeviceChannel;

/// <summary>
/// Background sweeper (T11): every 5 s, close connections whose last
/// heartbeat is older than 15 s and expire timed-out commands.
/// Heartbeat staleness is the upper bound for revocation propagation
/// when the network is down (docs/04 §10).
/// </summary>
public sealed class DeviceChannelSweeper : BackgroundService
{
    public static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan HeartbeatStaleAfter = TimeSpan.FromSeconds(15);

    private readonly DeviceConnectionRegistry _registry;
    private readonly CommandDispatcher _dispatcher;
    private readonly ILogger<DeviceChannelSweeper> _logger;

    public DeviceChannelSweeper(
        DeviceConnectionRegistry registry,
        CommandDispatcher dispatcher,
        ILogger<DeviceChannelSweeper> logger)
    {
        _registry = registry;
        _dispatcher = dispatcher;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(SweepInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            // Leases and consents expire LAZILY, on access
            // (ValidateForCommand / Acquire / Renew check the
            // monotonic deadline and mark the row). This sweeper
            // deliberately does no database work: a DI scope
            // created+disposed here killed the root provider under
            // WebApplicationFactory (ObjectDisposedException on
            // IServiceProvider in later requests), and
            // DbContextOptions is scoped, so a singleton sweeper
            // cannot hold it either. Stale rows that are never
            // accessed again accumulate until a maintenance pass.
            _dispatcher.ExpireStale();
            foreach (var stale in _registry.StaleConnections(HeartbeatStaleAfter))
            {
                _logger.LogInformation(
                    "Closing stale device connection {DeviceId} (no heartbeat for {Age}).",
                    stale.DeviceId, DateTimeOffset.UtcNow - stale.LastSeen);
                await DeviceConnectionRegistry.CloseAsync(stale.Socket,
                    (System.Net.WebSockets.WebSocketCloseStatus)Bridge.DeviceChannel.ChannelCloseCodes.HeartbeatTimeout,
                    "heartbeat timeout");
                _dispatcher.FailAll(stale.DeviceId, "device_disconnected");
            }
        }
    }
}
