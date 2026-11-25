using System.Text.Json.Nodes;
using Bridge.Relay;

namespace Bridge.Relay.Tests;

/// <summary>
/// Fake clock mirroring contract_lab's FakeClock (wall 1794567600.0,
/// monotonic 1000.0; Advance moves both; SetWall moves wall only, to
/// prove monotonic deadlines survive a wall-clock rollback).
/// </summary>
public sealed class FakeClock : IClock
{
    public double Wall { get; set; } = 1794567600.0;
    public double Monotonic { get; set; } = 1000.0;
    public double WallSeconds() => Wall;
    public double MonotonicSeconds() => Monotonic;
    public void Advance(double seconds) { Wall += seconds; Monotonic += seconds; }
}

/// <summary>
/// Fake desktop mirroring contract_lab's FakeDesktop. Fixture surface:
/// image 960x540, crop (-1920,0,1920,1080), virtual (-1920,0,3840,1080).
/// Execute asserts the journal row is 'executing' BEFORE the effect —
/// write-ahead persistence, not a promise.
/// </summary>
public sealed class FakeBackend : IRelayBackend
{
    public DesktopState State { get; set; } = new(1, "fixture-window-1", 0);
    public List<(string DeviceId, string Operation, Dictionary<string, object?> Payload)> Calls { get; } = new();
    public CommandJournal? Journal { get; set; }
    public Action? BeforeEffect { get; set; }
    public bool FailUnknown { get; set; }
    public bool ThrowNotImplementedOnExecute { get; set; }

    public CapturedSurface Capture(string deviceId) => new(
        State, 960, 540, -1920, 0, 1920, 1080, -1920, 0, 3840, 1080,
        CaptureKind: "fake-test-fixture");

    public DesktopState CurrentState(string deviceId) => State;

    public IReadOnlyDictionary<string, object?> Execute(
        string deviceId, string operation,
        IReadOnlyDictionary<string, object?> payload, Func<bool> shouldAbort)
    {
        BeforeEffect?.Invoke();
        if (ThrowNotImplementedOnExecute)
            throw new NotImplementedException("Real Windows input backend is not implemented");
        if (shouldAbort())
            throw new TimeoutException("Fixture interrupted before effect");
        // Write-ahead check: the journal row must already be 'executing'
        // before this fake effect is recorded.
        if (Journal is not null && Journal.CountByStatus("executing") != 1)
            throw new InvalidOperationException("Command was not durably accepted before effect");
        Calls.Add((deviceId, operation, new Dictionary<string, object?>(payload)));
        if (FailUnknown)
            throw new TimeoutException("Fixture effect occurred but response was lost");
        return new Dictionary<string, object?>
        {
            ["fixture_only"] = true,
            ["counter"] = Calls.Count,
        };
    }
}

/// <summary>Shared fixture: device + lease + observation, like the lab's setUp.</summary>
public sealed class RelayFixture : IDisposable
{
    public FakeClock Clock { get; } = new();
    public FakeBackend Backend { get; } = new();
    public CommandJournal Journal { get; }
    public CommandDispatcher Dispatcher { get; }
    public Lease Lease { get; }
    public string ObservationId { get; }

    public RelayFixture(string? path = null)
    {
        Journal = new CommandJournal(path ?? ":memory:");
        Backend.Journal = Journal;
        Dispatcher = new CommandDispatcher(Journal, Backend, Clock);
        Dispatcher.RegisterDevice("device-1", "owner-1", "muse-1");
        Lease = Dispatcher.AcquireLease("owner-1", "muse-1", "device-1");
        (ObservationId, _, _) = Dispatcher.Observe("owner-1", "muse-1", Lease);
    }

    public string CommandJson(string operation = "input.click", JsonNode? payload = null,
        long sequence = 1, string? commandId = null, string? observationId = null,
        double? issuedAt = null, double? expiresAt = null, string? leaseId = null,
        long? epoch = null)
    {
        var p = payload ?? new JsonObject { ["x"] = 100, ["y"] = 50 };
        var obj = new JsonObject
        {
            ["schema_version"] = "1.0",
            ["command_id"] = commandId ?? Guid.NewGuid().ToString("D"),
            ["device_id"] = "device-1",
            ["lease_id"] = leaseId ?? Lease.LeaseId,
            ["epoch"] = epoch ?? Lease.Epoch,
            ["sequence"] = sequence,
            ["operation"] = operation,
            ["observation_id"] = observationId ?? ObservationId,
            ["issued_at"] = CommandDispatcher.UtcString(issuedAt ?? Clock.Wall),
            ["expires_at"] = CommandDispatcher.UtcString(expiresAt ?? Clock.Wall + 10),
            ["payload"] = p,
        };
        return obj.ToJsonString();
    }

    public CommandReceipt Dispatch(string json) =>
        Dispatcher.Dispatch("owner-1", "muse-1", json);

    public void Dispose() => Journal.Dispose();
}
