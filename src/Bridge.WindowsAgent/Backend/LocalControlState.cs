using Bridge.DesktopCore;

namespace Bridge.WindowsAgent.Backend;

/// <summary>
/// Local control state (T19). Owned by the tray; the backend
/// checks it immediately before every side-effect batch.
/// Pause is LATCHED: only an explicit local resume clears it,
/// never a remote call. Every pause/resume/revocation bumps the
/// local epoch, which invalidates all observations and leases.
/// </summary>
public sealed class LocalControlState
{
    private long _epoch = 1;
    private readonly object _gate = new();
    private bool _paused;

    public GenerationTracker Generations { get; } = new();

    public bool Paused
    {
        get { lock (_gate) return _paused; }
    }

    public long Epoch => Interlocked.Read(ref _epoch);

    /// <summary>Local pause: latch it and end the current epoch.</summary>
    public void PauseLocally()
    {
        lock (_gate)
        {
            _paused = true;
        }
        Interlocked.Increment(ref _epoch);
        Generations.NoteLayoutChanged();
    }

    /// <summary>Local resume: only a local user action calls this.</summary>
    public void ResumeLocally()
    {
        lock (_gate)
        {
            _paused = false;
        }
        Interlocked.Increment(ref _epoch);
        Generations.NoteLayoutChanged();
    }

    /// <summary>Revocation / reconnect: end the epoch without pausing.</summary>
    public void BumpEpoch() => Interlocked.Increment(ref _epoch);
}
