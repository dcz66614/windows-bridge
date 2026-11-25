using System.Text.Json;
using Bridge.WindowsAgent.Contracts;

namespace Bridge.WindowsAgent.Backend;

/// <summary>
/// Routes one accepted command (already lease/epoch/sequence
/// validated by the relay and journaled by the desktop backend)
/// to the right local executor. File operations go to the
/// FileBackend; observe/input go to the desktop backend. This is
/// the single entry point the device channel loop will call once
/// the transport (T20/T23) exists; until then it is the seam the
/// file backend is exercised through.
/// </summary>
public sealed class AgentCommandRouter
{
    private readonly WindowsDesktopBackend _desktop;
    private readonly FileBackend _files;

    public AgentCommandRouter(WindowsDesktopBackend desktop, FileBackend files)
    {
        _desktop = desktop;
        _files = files;
    }

    public async Task<object> RouteAsync(
        ActionEnvelope envelope, AuthenticatedController controller, CancellationToken ct)
    {
        return envelope.Operation switch
        {
            OperationNames.FileList or OperationNames.FileRead or OperationNames.FileWrite or OperationNames.FileMove or OperationNames.FileMkdir
                => _files.Dispatch(envelope.Operation, envelope.Payload),
            _ => await _desktop.ExecuteAsync(envelope, controller, ct),
        };
    }
}
