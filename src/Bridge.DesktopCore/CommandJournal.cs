using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Bridge.DesktopCore;

/// <summary>
/// The agent's durable command journal (docs/04 §7). The accepted
/// record (with the canonical envelope hash + principal/controller/
/// lease/epoch binding) is committed BEFORE any side effect; the
/// state then moves to executing and finally to exactly one of
/// succeeded / failed / uncertain / cancelled. A reopened journal
/// with a command stuck in accepted/executing marks it uncertain —
/// it may or may not have happened. Same id + different binding →
/// conflict; same id + same binding → the stored receipt, no
/// re-execution. Mirrors Bridge.Relay's server-side semantics.
/// </summary>
public sealed class CommandJournal : IDisposable
{
    private readonly SqliteConnection _connection;

    public CommandJournal(string dbPath)
    {
        _connection = new SqliteConnection($"Data Source={dbPath}");
        _connection.Open();
        using var pragma = _connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;";
        pragma.ExecuteNonQuery();
        using var create = _connection.CreateCommand();
        create.CommandText = """
            CREATE TABLE IF NOT EXISTS commands (
                command_id TEXT PRIMARY KEY,
                device_id TEXT NOT NULL,
                lease_id TEXT NOT NULL,
                epoch INTEGER NOT NULL,
                sequence INTEGER NOT NULL,
                operation TEXT NOT NULL,
                binding_hash TEXT NOT NULL,
                principal_id TEXT NOT NULL,
                controller_id TEXT NOT NULL,
                status TEXT NOT NULL,
                result TEXT,
                created_at TEXT NOT NULL
            );
            """;
        create.ExecuteNonQuery();
        MarkInterruptedUncertain();
    }

    /// <summary>Canonical binding hash (contract_lab: sorted-key compact JSON).</summary>
    public static string BindingHash(
        string commandId, string deviceId, string leaseId, long epoch, long sequence,
        string operation, string payloadJson, string principalId, string controllerId)
    {
        var canonical = "{\"command_id\":" + JsonEncode(commandId) +
            ",\"controller_id\":" + JsonEncode(controllerId) +
            ",\"device_id\":" + JsonEncode(deviceId) +
            ",\"epoch\":" + epoch +
            ",\"lease_id\":" + JsonEncode(leaseId) +
            ",\"operation\":" + JsonEncode(operation) +
            ",\"payload\":" + payloadJson +
            ",\"principal_id\":" + JsonEncode(principalId) +
            ",\"sequence\":" + sequence + "}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();
    }

    private static string JsonEncode(string value) =>
        System.Text.Json.JsonSerializer.Serialize(value);

    public sealed record Receipt(string CommandId, string Status, string? Result, bool Replay);

    /// <summary>
    /// Accept a command: returns (accepted=true) for a new command,
    /// (accepted=false, receipt) for a replay, or throws
    /// <see cref="IdempotencyConflictException"/> for a binding mismatch.
    /// </summary>
    public (bool Accepted, Receipt? Replay) Accept(
        string commandId, string deviceId, string leaseId, long epoch, long sequence,
        string operation, string bindingHash, string principalId, string controllerId)
    {
        using var select = _connection.CreateCommand();
        select.CommandText = "SELECT binding_hash, status, result FROM commands WHERE command_id = $id";
        select.Parameters.AddWithValue("$id", commandId);
        using var reader = select.ExecuteReader();
        if (reader.Read())
        {
            var storedHash = reader.GetString(0);
            var status = reader.GetString(1);
            var result = reader.IsDBNull(2) ? null : reader.GetString(2);
            if (storedHash != bindingHash)
                throw new IdempotencyConflictException(commandId);
            return (false, new Receipt(commandId, status, result, Replay: true));
        }
        using var insert = _connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO commands
                (command_id, device_id, lease_id, epoch, sequence, operation,
                 binding_hash, principal_id, controller_id, status, created_at)
            VALUES ($id, $device, $lease, $epoch, $seq, $op, $hash, $principal, $controller, 'accepted', $now)
            """;
        insert.Parameters.AddWithValue("$id", commandId);
        insert.Parameters.AddWithValue("$device", deviceId);
        insert.Parameters.AddWithValue("$lease", leaseId);
        insert.Parameters.AddWithValue("$epoch", epoch);
        insert.Parameters.AddWithValue("$seq", sequence);
        insert.Parameters.AddWithValue("$op", operation);
        insert.Parameters.AddWithValue("$hash", bindingHash);
        insert.Parameters.AddWithValue("$principal", principalId);
        insert.Parameters.AddWithValue("$controller", controllerId);
        insert.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        insert.ExecuteNonQuery();
        return (true, null);
    }

    public void MarkExecuting(string commandId) => SetStatus(commandId, "executing", null);

    public void Finish(string commandId, string status, string? result)
    {
        if (status is not ("succeeded" or "failed" or "uncertain" or "cancelled"))
            throw new ArgumentException($"Invalid terminal status: {status}", nameof(status));
        SetStatus(commandId, status, result);
    }

    public Receipt? Lookup(string commandId)
    {
        using var select = _connection.CreateCommand();
        select.CommandText = "SELECT status, result FROM commands WHERE command_id = $id";
        select.Parameters.AddWithValue("$id", commandId);
        using var reader = select.ExecuteReader();
        if (!reader.Read())
            return null;
        return new Receipt(commandId, reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1), Replay: false);
    }

    private void SetStatus(string commandId, string status, string? result)
    {
        using var update = _connection.CreateCommand();
        update.CommandText = "UPDATE commands SET status = $status, result = $result WHERE command_id = $id";
        update.Parameters.AddWithValue("$status", status);
        update.Parameters.AddWithValue("$result", (object?)result ?? DBNull.Value);
        update.Parameters.AddWithValue("$id", commandId);
        update.ExecuteNonQuery();
    }

    private void MarkInterruptedUncertain()
    {
        using var update = _connection.CreateCommand();
        update.CommandText = "UPDATE commands SET status = 'uncertain' WHERE status IN ('accepted', 'executing')";
        update.ExecuteNonQuery();
    }

    public void Dispose() => _connection.Dispose();
}

public sealed class IdempotencyConflictException(string commandId)
    : InvalidOperationException($"command_id {commandId} was already used with a different binding.");
