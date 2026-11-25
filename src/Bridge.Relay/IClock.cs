using System.Diagnostics;

namespace Bridge.Relay;

/// <summary>Wall + monotonic clocks. The lab keeps both: wall for TTL/expiry,
/// monotonic for deadlines so a wall-clock rollback cannot resurrect a
/// lease or observation (contract_lab test_monotonic_lease_expiry_survives_wall_clock_rollback).
/// </summary>
public interface IClock
{
    double WallSeconds();
    double MonotonicSeconds();
}

public sealed class SystemClock : IClock
{
    public double WallSeconds() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
    public double MonotonicSeconds() => (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency;
}
