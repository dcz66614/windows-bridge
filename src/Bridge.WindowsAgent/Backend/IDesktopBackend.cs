using Bridge.WindowsAgent.Contracts;

namespace Bridge.WindowsAgent.Backend;

/// <summary>
/// Process-internal boundary, never a directly exposed network API.
/// Before ExecuteAsync the controller must authenticate, verify local approval/lease/fresh
/// observation, atomically persist accepted to a durable SQLite journal, and serialize input.
/// Re-check the local pause/epoch immediately before each side-effect batch.
/// CancellationToken cannot magically abort a blocked COM call: use an isolated UIA worker.
/// No implementation, journal, authorization gate or transport is included in this starter.
/// </summary>
public interface IDesktopBackend : IAsyncDisposable
{
    Task<DesktopCapabilities> ProbeAsync(CancellationToken cancellationToken);
    Task<Observation> ObserveAsync(InternalObservationRequest request, CancellationToken cancellationToken);
    Task<ActionResult> ExecuteAsync(ActionEnvelope request, AuthenticatedController controller,
        CancellationToken cancellationToken);

    /// <summary>
    /// Release only keys/buttons held by this backend. This does not undo completed effects.
    /// A future implementation must return failures and must not claim a global emergency stop.
    /// </summary>
    Task ReleaseOwnedInputsAsync(CancellationToken cancellationToken);
}
