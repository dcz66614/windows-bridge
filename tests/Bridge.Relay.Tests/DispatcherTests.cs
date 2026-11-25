using System.Text.Json.Nodes;
using Bridge.Relay;
using Xunit;

namespace Bridge.Relay.Tests;

/// <summary>
/// T08 acceptance, mirroring contract_lab's test_bridge_contract.py
/// case-for-case where the semantics apply. No real GUI/network.
/// </summary>
public sealed class DispatcherTests
{
    private static void Rejects(string code, Action fn)
    {
        var ex = Assert.Throws<ContractError>(fn);
        Assert.Equal(code, ex.Code);
    }

    [Fact]
    public void FixtureClickMapsNegativeOriginAndScaledImage()
    {
        using var f = new RelayFixture();
        var result = f.Dispatch(f.CommandJson());
        Assert.Equal("succeeded", result.Status);
        Assert.True(result.Result!["receipt"]!["fixture_only"]!.GetValue<bool>());
        var payload = f.Backend.Calls[0].Payload;
        Assert.Equal(-1720L, payload["native_x"]);
        Assert.Equal(100L, payload["native_y"]);
        Assert.True((bool)payload["virtual_desktop"]!);
        Assert.InRange((long)payload["normalized_x"]!, 0, 65535);
    }

    [Fact]
    public void FloorMappingAtImageEdges()
    {
        using var f = new RelayFixture();
        var cases = new (int X, int Y, long Nx, long Ny)[] { (0, 0, -1920, 0), (959, 539, -2, 1078) };
        for (var i = 0; i < cases.Length; i++)
        {
            var (x, y, nx, ny) = cases[i];
            // Fresh observation per sequence, like the lab test.
            var (obsId, _, _) = f.Dispatcher.Observe("owner-1", "muse-1", f.Lease);
            var json = f.CommandJson(payload: new JsonObject { ["x"] = x, ["y"] = y },
                sequence: i + 1, observationId: obsId);
            var result = f.Dispatch(json);
            Assert.Equal("succeeded", result.Status);
            var payload = f.Backend.Calls[^1].Payload;
            Assert.Equal(nx, payload["native_x"]);
            Assert.Equal(ny, payload["native_y"]);
        }
    }

    [Fact]
    public void DuplicateIsReturnedWithoutSecondEffect()
    {
        using var f = new RelayFixture();
        var json = f.CommandJson();
        f.Dispatch(json);
        for (var i = 0; i < 10; i++)
            Assert.True(f.Dispatch(json).Duplicate);
        Assert.Single(f.Backend.Calls);
    }

    [Fact]
    public void KeyOrderDoesNotChangeCanonicalDedup()
    {
        using var f = new RelayFixture();
        var json = f.CommandJson();
        f.Dispatch(json);
        // Same envelope, keys reversed at both levels.
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var reordered = new JsonObject();
        foreach (var prop in doc.RootElement.EnumerateObject().Reverse())
        {
            if (prop.Name == "payload")
                reordered["payload"] = new JsonObject { ["y"] = 50, ["x"] = 100 };
            else
                reordered[prop.Name] = JsonNode.Parse(prop.Value.GetRawText());
        }
        Assert.True(f.Dispatch(reordered.ToJsonString()).Duplicate);
        Assert.Single(f.Backend.Calls);
    }

    [Fact]
    public void SameIdChangedPayloadRejected()
    {
        using var f = new RelayFixture();
        var commandId = Guid.NewGuid().ToString("D");
        f.Dispatch(f.CommandJson(commandId: commandId));
        var changed = f.CommandJson(
            payload: new JsonObject { ["x"] = 3, ["y"] = 50 }, commandId: commandId);
        Rejects("IDEMPOTENCY_CONFLICT", () => f.Dispatch(changed));
        Assert.Single(f.Backend.Calls);
    }

    [Fact]
    public void SameIdChangedLeaseRejected()
    {
        using var f = new RelayFixture();
        var commandId = Guid.NewGuid().ToString("D");
        f.Dispatch(f.CommandJson(commandId: commandId));
        var changed = f.CommandJson(commandId: commandId, leaseId: Guid.NewGuid().ToString("D"));
        Rejects("IDEMPOTENCY_CONFLICT", () => f.Dispatch(changed));
    }

    [Fact]
    public void WrongOwnerCannotDispatch()
    {
        using var f = new RelayFixture();
        Rejects("FORBIDDEN", () =>
            f.Dispatcher.Dispatch("other-owner", "muse-1", f.CommandJson()));
        Assert.Empty(f.Backend.Calls);
    }

    [Fact]
    public void ExpiredCommandNeverExecutes()
    {
        using var f = new RelayFixture();
        var json = f.CommandJson();
        f.Clock.Advance(11);
        Rejects("COMMAND_EXPIRED_OR_FUTURE", () => f.Dispatch(json));
        Assert.Empty(f.Backend.Calls);
    }

    [Fact]
    public void OutOfSequenceNeverExecutes()
    {
        using var f = new RelayFixture();
        Rejects("SEQUENCE_MISMATCH", () => f.Dispatch(f.CommandJson(sequence: 2)));
        Assert.Empty(f.Backend.Calls);
    }

    [Fact]
    public void DuplicateAfterExpiryReturnsStatusWithoutEffect()
    {
        using var f = new RelayFixture();
        var json = f.CommandJson();
        var first = f.Dispatch(json);
        Assert.Equal("succeeded", first.Status);
        f.Clock.Advance(11); // past the 10s TTL
        var again = f.Dispatch(json);
        Assert.True(again.Duplicate);
        Assert.Equal("succeeded", again.Status);
        Assert.Single(f.Backend.Calls);
    }

    [Fact]
    public void UnknownBackendFailureIsUncertainAndDeviceRecovers()
    {
        using var f = new RelayFixture();
        f.Backend.FailUnknown = true;
        var result = f.Dispatch(f.CommandJson());
        Assert.Equal("uncertain", result.Status);
        Assert.Equal("BACKEND_OUTCOME_UNKNOWN", result.Result!["code"]!.GetValue<string>());
        // Device is free again: next sequence with a fresh observation works.
        f.Backend.FailUnknown = false;
        var (obsId, _, _) = f.Dispatcher.Observe("owner-1", "muse-1", f.Lease);
        var next = f.Dispatch(f.CommandJson(sequence: 2, observationId: obsId));
        Assert.Equal("succeeded", next.Status);
    }

    [Fact]
    public void UnavailableBackendFailsWithoutEffect()
    {
        using var f = new RelayFixture();
        f.Backend.ThrowNotImplementedOnExecute = true;
        var result = f.Dispatch(f.CommandJson());
        Assert.Equal("failed", result.Status);
        Assert.Equal("BACKEND_NOT_IMPLEMENTED", result.Result!["code"]!.GetValue<string>());
        Assert.Empty(f.Backend.Calls);
    }

    [Fact]
    public void StaleObservationRejectedAfterConsumption()
    {
        using var f = new RelayFixture();
        f.Dispatch(f.CommandJson()); // consumes the observation
        Rejects("STALE_OBSERVATION", () =>
            f.Dispatch(f.CommandJson(sequence: 2))); // same observation id
    }

    [Fact]
    public void AbortDuringExecuteIsUncertainWithoutEffect()
    {
        // Mirrors the lab: a pause (or stop) landing inside execute makes
        // the outcome unknown — the OS effect may already have occurred —
        // so the status is uncertain, never a silent success.
        using var f = new RelayFixture();
        f.Backend.BeforeEffect = () => f.Dispatcher.Pause("device-1");
        var result = f.Dispatch(f.CommandJson());
        Assert.Equal("uncertain", result.Status);
        Assert.Equal("BACKEND_OUTCOME_UNKNOWN", result.Result!["code"]!.GetValue<string>());
        Assert.Empty(f.Backend.Calls);
    }

    [Fact]
    public void WallClockRollbackDoesNotResurrectLease()
    {
        using var f = new RelayFixture();
        f.Clock.Wall -= 100; // wall goes back; monotonic deadline still governs
        var result = f.Dispatch(f.CommandJson(
            issuedAt: f.Clock.Wall, expiresAt: f.Clock.Wall + 10));
        Assert.Equal("succeeded", result.Status);
    }

    [Fact]
    public void RestartMarksUnfinishedUncertainWithoutReplay()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bridge-relay-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var db = Path.Combine(dir, "ledger.sqlite");
        try
        {
            string commandId;
            {
                // Crash mid-execution: journal row stays 'executing'.
                using var f = new RelayFixture(db);
                commandId = Guid.NewGuid().ToString("D");
                var row = new CommandJournal.CommandRow(
                    commandId, "device-1", "owner-1", "muse-1",
                    f.Lease.LeaseId, f.Lease.Epoch, "deadbeef", "executing", "{}");
                f.Journal.InsertExecuting(row, f.ObservationId);
            }
            // Reopen: recovery marks it uncertain, epoch bumps, lease clears.
            using var journal2 = new CommandJournal(db);
            var stored = journal2.GetCommandRowById(commandId)!;
            Assert.Equal("uncertain", stored.Status);
            Assert.Contains("PROCESS_RESTART_OUTCOME_UNKNOWN", stored.ResultJson);
            var device = journal2.GetDevice("device-1")!;
            Assert.Equal(2, device.Epoch);
            Assert.Null(device.LeaseId);
            Assert.Null(device.ActiveCommandId);

            // Same command re-dispatched on the recovered journal returns
            // the uncertain receipt without a second effect.
            var clock = new FakeClock();
            var backend = new FakeBackend();
            var dispatcher = new CommandDispatcher(journal2, backend, clock);
            var lease = dispatcher.AcquireLease("owner-1", "muse-1", "device-1");
            var (obsId, _, _) = dispatcher.Observe("owner-1", "muse-1", lease);
            var json = new JsonObject
            {
                ["schema_version"] = "1.0",
                ["command_id"] = commandId,
                ["device_id"] = "device-1",
                ["lease_id"] = lease.LeaseId,
                ["epoch"] = lease.Epoch,
                ["sequence"] = 1,
                ["operation"] = "input.click",
                ["observation_id"] = obsId,
                ["issued_at"] = CommandDispatcher.UtcString(clock.Wall),
                ["expires_at"] = CommandDispatcher.UtcString(clock.Wall + 10),
                ["payload"] = new JsonObject { ["x"] = 1, ["y"] = 1 },
            }.ToJsonString();
            // Note: binding differs (new lease/epoch) so this is a conflict,
            // proving the crashed command is NOT silently replayed. The
            // stored receipt is readable via GetCommand instead.
            Rejects("IDEMPOTENCY_CONFLICT", () => dispatcher.Dispatch("owner-1", "muse-1", json));
            Assert.Empty(backend.Calls);
            var stored2 = dispatcher.GetCommand("owner-1", "muse-1", "device-1", commandId);
            Assert.Equal("uncertain", stored2.Status);
        }
        finally { Directory.Delete(dir, true); }
    }
}
