using System.Collections.Concurrent;

namespace Bridge.RelayService.Data;

/// <summary>
/// Tiny in-memory fixed-window limiter for the pairing endpoints
/// (T10: limits apply per IP/account/pairing record). Single instance
/// only — the relay is single-instance by design (docs/04).
/// </summary>
public sealed class PairingRateLimiter
{
    private sealed record Entry(int Count, DateTimeOffset WindowStart);
    private readonly ConcurrentDictionary<string, Entry> _entries = new();
    private readonly object _gate = new();

    public bool TryConsume(string key, int limit, TimeSpan window)
    {
        var now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var entry) &&
                now - entry.WindowStart < window)
            {
                if (entry.Count >= limit)
                    return false;
                _entries[key] = entry with { Count = entry.Count + 1 };
                return true;
            }
            _entries[key] = new Entry(1, now);
            return true;
        }
    }
}
