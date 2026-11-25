using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Bridge.Relay;

/// <summary>
/// SQLite journal for the relay (T08), schema-identical to contract_lab:
/// devices / observations / commands. One connection, one lock — the
/// relay is single-instance by design (docs/04 §7).
///
/// Durability rules (lab __init__ + kit fixed stack):
///  - PRAGMA foreign_keys=ON, synchronous=FULL, journal_mode=WAL.
///  - Journal row is INSERTed with status 'executing' BEFORE any side
///    effect, in the same transaction that bumps next_sequence, sets
///    active_command_id and consumes the observation.
///  - On open, any command left in 'accepted'/'executing' is marked
///    'uncertain' with PROCESS_RESTART_OUTCOME_UNKNOWN, devices get
///    epoch+1 with leases cleared, observations are deleted. A restart
///    never replays an effect whose outcome is unknown.
/// </summary>
public sealed class CommandJournal : IDisposable
{
    public const string RestartUnknownCode = "PROCESS_RESTART_OUTCOME_UNKNOWN";

    private readonly SqliteConnection _db;
    private readonly object _gate = new();

    public sealed record DeviceRow(
        string DeviceId, string OwnerId, string ControllerId, long Epoch,
        bool Paused, string? LeaseId, double? LeaseUntil,
        long NextSequence, string? ActiveCommandId);

    public sealed record ObservationRow(
        string ObservationId, string DeviceId, string LeaseId, long Epoch,
        double ExpiresAt, string SurfaceJson, bool Consumed);

    public sealed record CommandRow(
        string CommandId, string DeviceId, string OwnerId, string ControllerId,
        string LeaseId, long Epoch, string EnvelopeHash, string Status, string ResultJson);

    public CommandJournal(string path)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = path == ":memory:" ? SqliteOpenMode.Memory : SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        };
        _db = new SqliteConnection(builder.ToString());
        _db.Open();
        using var pragma = _db.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys=ON; PRAGMA synchronous=FULL; PRAGMA journal_mode=WAL;";
        pragma.ExecuteNonQuery();
        using var schema = _db.CreateCommand();
        schema.CommandText = """
            CREATE TABLE IF NOT EXISTS devices (
              device_id TEXT PRIMARY KEY, owner_id TEXT NOT NULL, controller_id TEXT NOT NULL,
              epoch INTEGER NOT NULL DEFAULT 0, paused INTEGER NOT NULL DEFAULT 0,
              lease_id TEXT, lease_until REAL, next_sequence INTEGER NOT NULL DEFAULT 1,
              active_command_id TEXT);
            CREATE TABLE IF NOT EXISTS observations (
              observation_id TEXT PRIMARY KEY, device_id TEXT NOT NULL, lease_id TEXT NOT NULL,
              epoch INTEGER NOT NULL, expires_at REAL NOT NULL, surface_json TEXT NOT NULL,
              consumed INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS commands (
              command_id TEXT PRIMARY KEY, device_id TEXT NOT NULL, owner_id TEXT NOT NULL,
              controller_id TEXT NOT NULL, lease_id TEXT NOT NULL, epoch INTEGER NOT NULL,
              envelope_hash TEXT NOT NULL, status TEXT NOT NULL, result_json TEXT NOT NULL);
            """;
        schema.ExecuteNonQuery();
        RecoverFromRestart();
    }

    private void RecoverFromRestart()
    {
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();
            using var cmd = _db.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText =
                "UPDATE commands SET status='uncertain', result_json=@r " +
                "WHERE status IN ('accepted','executing')";
            cmd.Parameters.AddWithValue("@r", JsonSerializer.Serialize(
                new Dictionary<string, string> { ["code"] = RestartUnknownCode }));
            cmd.ExecuteNonQuery();
            cmd.CommandText =
                "UPDATE devices SET epoch=epoch+1, lease_id=NULL, lease_until=NULL, active_command_id=NULL";
            cmd.Parameters.Clear();
            cmd.ExecuteNonQuery();
            cmd.CommandText = "DELETE FROM observations";
            cmd.ExecuteNonQuery();
            tx.Commit();
        }
    }

    public void RegisterDevice(string deviceId, string ownerId, string controllerId)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText =
                "INSERT INTO devices(device_id,owner_id,controller_id) VALUES (@d,@o,@c)";
            cmd.Parameters.AddWithValue("@d", deviceId);
            cmd.Parameters.AddWithValue("@o", ownerId);
            cmd.Parameters.AddWithValue("@c", controllerId);
            try { cmd.ExecuteNonQuery(); }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
            { throw new ContractError("DEVICE_ALREADY_REGISTERED", $"Device already registered: {deviceId}."); }
        }
    }

    public DeviceRow? GetDevice(string deviceId)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT * FROM devices WHERE device_id=@d";
            cmd.Parameters.AddWithValue("@d", deviceId);
            using var r = cmd.ExecuteReader();
            return r.Read() ? ReadDevice(r) : null;
        }
    }

    public void UpdateDeviceLease(string deviceId, string leaseId, double leaseUntil, long epoch)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText =
                "UPDATE devices SET lease_id=@l,lease_until=@u,epoch=@e,next_sequence=1 WHERE device_id=@d";
            cmd.Parameters.AddWithValue("@l", leaseId);
            cmd.Parameters.AddWithValue("@u", leaseUntil);
            cmd.Parameters.AddWithValue("@e", epoch);
            cmd.Parameters.AddWithValue("@d", deviceId);
            cmd.ExecuteNonQuery();
        }
    }

    public void SetPaused(string deviceId, bool paused)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = paused
                ? "UPDATE devices SET paused=1,epoch=epoch+1,lease_id=NULL,lease_until=NULL WHERE device_id=@d"
                : "UPDATE devices SET paused=0 WHERE device_id=@d";
            cmd.Parameters.AddWithValue("@d", deviceId);
            cmd.ExecuteNonQuery();
        }
    }

    public void InsertObservation(ObservationRow obs)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText =
                "INSERT INTO observations VALUES(@o,@d,@l,@e,@x,@s,0)";
            cmd.Parameters.AddWithValue("@o", obs.ObservationId);
            cmd.Parameters.AddWithValue("@d", obs.DeviceId);
            cmd.Parameters.AddWithValue("@l", obs.LeaseId);
            cmd.Parameters.AddWithValue("@e", obs.Epoch);
            cmd.Parameters.AddWithValue("@x", obs.ExpiresAt);
            cmd.Parameters.AddWithValue("@s", obs.SurfaceJson);
            cmd.ExecuteNonQuery();
        }
    }

    public ObservationRow? GetObservation(string observationId)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT * FROM observations WHERE observation_id=@o";
            cmd.Parameters.AddWithValue("@o", observationId);
            using var r = cmd.ExecuteReader();
            return r.Read() ? ReadObservation(r) : null;
        }
    }

    public CommandRow? GetCommandRow(string commandId, string deviceId)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT * FROM commands WHERE command_id=@c AND device_id=@d";
            cmd.Parameters.AddWithValue("@c", commandId);
            cmd.Parameters.AddWithValue("@d", deviceId);
            using var r = cmd.ExecuteReader();
            return r.Read() ? ReadCommand(r) : null;
        }
    }

    public CommandRow? GetCommandRowById(string commandId)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT * FROM commands WHERE command_id=@c";
            cmd.Parameters.AddWithValue("@c", commandId);
            using var r = cmd.ExecuteReader();
            return r.Read() ? ReadCommand(r) : null;
        }
    }

    /// <summary>
    /// Journal INSERT before any side effect (lab dispatch, BEGIN IMMEDIATE):
    /// command row 'executing', next_sequence+1, active_command_id set,
    /// observation consumed. All-or-nothing.
    /// </summary>
    public void InsertExecuting(CommandRow row, string observationId)
    {
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();
            try
            {
                using var cmd = _db.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText =
                    "INSERT INTO commands VALUES(@c,@d,@o,@t,@l,@e,@h,'executing','{}')";
                cmd.Parameters.AddWithValue("@c", row.CommandId);
                cmd.Parameters.AddWithValue("@d", row.DeviceId);
                cmd.Parameters.AddWithValue("@o", row.OwnerId);
                cmd.Parameters.AddWithValue("@t", row.ControllerId);
                cmd.Parameters.AddWithValue("@l", row.LeaseId);
                cmd.Parameters.AddWithValue("@e", row.Epoch);
                cmd.Parameters.AddWithValue("@h", row.EnvelopeHash);
                cmd.ExecuteNonQuery();
                cmd.CommandText =
                    "UPDATE devices SET next_sequence=next_sequence+1,active_command_id=@c WHERE device_id=@d";
                cmd.Parameters.Clear();
                cmd.Parameters.AddWithValue("@c", row.CommandId);
                cmd.Parameters.AddWithValue("@d", row.DeviceId);
                cmd.ExecuteNonQuery();
                cmd.CommandText =
                    "UPDATE observations SET consumed=1 WHERE observation_id=@o";
                cmd.Parameters.Clear();
                cmd.Parameters.AddWithValue("@o", observationId);
                cmd.ExecuteNonQuery();
                tx.Commit();
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }
    }

    /// <summary>
    /// Terminal transition. Only 'executing' (or 'accepted') may move to a
    /// terminal status; anything else is a journal bug, fail loudly.
    /// </summary>
    public void Finish(string commandId, string deviceId, string status, string resultJson)
    {
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();
            try
            {
                using var cmd = _db.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "SELECT status FROM commands WHERE command_id=@c AND device_id=@d";
                cmd.Parameters.AddWithValue("@c", commandId);
                cmd.Parameters.AddWithValue("@d", deviceId);
                var current = cmd.ExecuteScalar() as string
                    ?? throw new ContractError("NOT_FOUND", $"Unknown command: {commandId}.");
                if (current is not ("executing" or "accepted"))
                    throw new InvalidOperationException(
                        $"Illegal command transition: {current} -> {status} for {commandId}.");
                cmd.CommandText = "UPDATE commands SET status=@s,result_json=@r WHERE command_id=@c";
                cmd.Parameters.Clear();
                cmd.Parameters.AddWithValue("@s", status);
                cmd.Parameters.AddWithValue("@r", resultJson);
                cmd.Parameters.AddWithValue("@c", commandId);
                cmd.ExecuteNonQuery();
                cmd.CommandText =
                    "UPDATE devices SET active_command_id=NULL WHERE device_id=@d AND active_command_id=@c";
                cmd.Parameters.Clear();
                cmd.Parameters.AddWithValue("@d", deviceId);
                cmd.Parameters.AddWithValue("@c", commandId);
                cmd.ExecuteNonQuery();
                tx.Commit();
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }
    }

    private static DeviceRow ReadDevice(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt64(3),
        r.GetInt64(4) != 0,
        r.IsDBNull(5) ? null : r.GetString(5),
        r.IsDBNull(6) ? null : r.GetDouble(6),
        r.GetInt64(7),
        r.IsDBNull(8) ? null : r.GetString(8));

    private static ObservationRow ReadObservation(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt64(3),
        r.GetDouble(4), r.GetString(5), r.GetInt64(6) != 0);

    private static CommandRow ReadCommand(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3),
        r.GetString(4), r.GetInt64(5), r.GetString(6), r.GetString(7), r.GetString(8));

    public int CountByStatus(string status)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM commands WHERE status=@s";
            cmd.Parameters.AddWithValue("@s", status);
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
    }

    public void Dispose() => _db.Dispose();
}
