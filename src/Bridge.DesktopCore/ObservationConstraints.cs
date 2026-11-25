namespace Bridge.DesktopCore;

/// <summary>
/// Observation freshness and generation tracking (docs/04 §8).
/// An observation is usable for input only while it is fresh
/// (default 30 s), the epoch and lease match, and the layout,
/// foreground and input generations all match the live desktop.
/// One accepted input consumes the observation.
/// </summary>
public static class ObservationConstraints
{
    public static readonly TimeSpan DefaultMaxAge = TimeSpan.FromSeconds(30);

    public sealed record Generations(long LayoutRevision, long ForegroundGeneration, long InputGeneration);

    public static bool IsFresh(DateTimeOffset capturedAt, DateTimeOffset now, TimeSpan? maxAge = null) =>
        now - capturedAt <= (maxAge ?? DefaultMaxAge) && capturedAt <= now.AddSeconds(2);

    public static bool GenerationsMatch(Generations observed, Generations current) =>
        observed == current;
}

/// <summary>
/// Monotonic generation counters for the live desktop. The tray
/// owns one instance; the worker reports changes through it.
/// </summary>
public sealed class GenerationTracker
{
    private long _layoutRevision;
    private long _foregroundGeneration;
    private long _inputGeneration;

    public ObservationConstraints.Generations Current => new(
        Interlocked.Read(ref _layoutRevision),
        Interlocked.Read(ref _foregroundGeneration),
        Interlocked.Read(ref _inputGeneration));

    public void NoteLayoutChanged() => Interlocked.Increment(ref _layoutRevision);
    public void NoteForegroundChanged() => Interlocked.Increment(ref _foregroundGeneration);
    public void NoteInputExecuted() => Interlocked.Increment(ref _inputGeneration);
}
