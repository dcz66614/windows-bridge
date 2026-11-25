using Bridge.WindowsAgent.Contracts;

namespace Bridge.WindowsAgent.Backend;

/// <summary>Fail-closed placeholder: no fabricated screenshots, capabilities or successful input.</summary>
public sealed class NotImplementedDesktopBackend : IDesktopBackend
{
    private static NotSupportedException Missing() => new(
        "NOT_IMPLEMENTED: The starter does not capture or control a Windows desktop.");

    public Task<DesktopCapabilities> ProbeAsync(CancellationToken cancellationToken) => throw Missing();
    public Task<Observation> ObserveAsync(InternalObservationRequest request,
        CancellationToken cancellationToken) => throw Missing();
    public Task<ActionResult> ExecuteAsync(ActionEnvelope request, AuthenticatedController controller,
        CancellationToken cancellationToken) => throw Missing();
    public Task ReleaseOwnedInputsAsync(CancellationToken cancellationToken) => throw Missing();

    // Disposal has no work because this placeholder never acquires native resources.
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
