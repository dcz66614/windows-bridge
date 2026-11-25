namespace Bridge.Relay;

/// <summary>
/// Desktop state + captured surface, mirroring contract_lab's frozen
/// dataclasses. The surface carries metadata only — a real PNG is
/// transported separately, never faked as fixture bytes.
/// </summary>
public sealed record DesktopState(
    long LayoutRevision,
    string ForegroundToken = "",
    long InputGeneration = 0,
    bool Unlocked = true,
    long ForegroundGeneration = 0);

public sealed record CapturedSurface(
    DesktopState State,
    long ImageWidth,
    long ImageHeight,
    long CropX,
    long CropY,
    long CropWidth,
    long CropHeight,
    long VirtualX,
    long VirtualY,
    long VirtualWidth,
    long VirtualHeight,
    string CaptureKind = "native");

/// <summary>
/// Native Windows backend (capture / state / input). Implemented by the
/// tray agent on Windows; the lab's UnavailableDesktop equivalent throws
/// NotImplementedException so a missing backend is explicit, never silent.
/// </summary>
public interface IRelayBackend
{
    CapturedSurface Capture(string deviceId);
    DesktopState CurrentState(string deviceId);
    IReadOnlyDictionary<string, object?> Execute(
        string deviceId, string operation,
        IReadOnlyDictionary<string, object?> payload, Func<bool> shouldAbort);
}

public sealed class UnavailableBackend : IRelayBackend
{
    public CapturedSurface Capture(string deviceId) =>
        throw new NotImplementedException("Real Windows capture backend is not implemented");
    public DesktopState CurrentState(string deviceId) =>
        throw new NotImplementedException("Real Windows state backend is not implemented");
    public IReadOnlyDictionary<string, object?> Execute(
        string deviceId, string operation,
        IReadOnlyDictionary<string, object?> payload, Func<bool> shouldAbort) =>
        throw new NotImplementedException("Real Windows input backend is not implemented");
}
