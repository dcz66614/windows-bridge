using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bridge.RelayService.Data;
using Bridge.RelayService.DeviceChannel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Validation.AspNetCore;

namespace Bridge.RelayService.Controllers;

/// <summary>
/// Control commands (T12). The full command envelope (docs/04 §6):
/// exactly 11 fields, strict validation, clock-skew check, TTL,
/// lease + epoch + sequence, idempotency via stored receipts.
/// Error shape is always {error:{code, message, next_action}}.
/// </summary>
[ApiController]
[Route("api/v1/devices/{deviceId}/commands")]
[Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme)]
public sealed class CommandsController : ControllerBase
{
    public static readonly string[] AllowedFields =
    {
        "schema_version", "command_id", "device_id", "lease_id", "epoch",
        "sequence", "operation", "observation_id", "issued_at", "expires_at", "payload",
    };

    public const int MaxTtlSeconds = 30;
    public const int MaxClockSkewSeconds = 2;

    private readonly ApplicationDbContext _db;
    private readonly CommandDispatcher _dispatcher;
    private readonly LeaseManager _leases;

    public CommandsController(
        ApplicationDbContext db,
        CommandDispatcher dispatcher,
        LeaseManager leases)
    {
        _db = db;
        _dispatcher = dispatcher;
        _leases = leases;
    }

    /// <summary>Operation → required scope.</summary>
    public static string? RequiredScope(string operation) => operation switch
    {
        "test.echo" => "desktop.observe",
        "observe.screenshot" => "desktop.observe",
        "observe.ui" => "desktop.observe",
        "input.click" => "desktop.input",
        "input.type_text" => "desktop.input",
        "input.key" => "desktop.input",
        "file.list" => "desktop.observe",
        "file.read" => "desktop.observe",
        "file.write" => "desktop.input",
        "file.move" => "desktop.input",
        "file.mkdir" => "desktop.input",
        _ => null,
    };

    private static IActionResult Err(string code, int status, string nextAction) =>
        new ObjectResult(new
        {
            error = new { code, message = code, next_action = nextAction },
        })
        { StatusCode = status };

    [HttpPost]
    public async Task<IActionResult> Send(string deviceId, [FromBody] JsonElement envelope)
    {
        var owner = User.FindFirst("sub")?.Value;
        if (owner is null)
            return Unauthorized();
        var controllerId =
            (User.FindFirst("client_id")?.Value ?? "unknown") + ":" + owner;

        // ---- strict envelope validation (docs/04 §6) ----
        if (envelope.ValueKind != JsonValueKind.Object)
            return Err("bad_envelope", 400, "none");
        var fields = envelope.EnumerateObject().Select(p => p.Name).ToList();
        if (fields.Any(f => !AllowedFields.Contains(f)))
            return Err("unknown_field", 400, "none");
        var required = AllowedFields.Where(f => f != "observation_id");
        if (required.Any(f => !fields.Contains(f)))
            return Err("missing_field", 400, "none");
        if (envelope.GetProperty("schema_version").GetString() != "1.0")
            return Err("bad_schema_version", 400, "none");
        if (!Guid.TryParse(envelope.GetProperty("command_id").GetString(), out var commandId) ||
            !Guid.TryParse(envelope.GetProperty("lease_id").GetString(), out _))
            return Err("bad_id", 400, "none");
        if (envelope.GetProperty("device_id").GetString() != deviceId)
            return Err("device_mismatch", 400, "none");
        var operation = envelope.GetProperty("operation").GetString()!;
        var requiredScope = RequiredScope(operation);
        if (requiredScope is null)
            return Err("unknown_operation", 400, "none");
        if (!envelope.GetProperty("epoch").TryGetInt32(out var epoch) || epoch < 1)
            return Err("bad_epoch", 400, "none");
        if (!envelope.GetProperty("sequence").TryGetInt32(out var sequence) || sequence < 1)
            return Err("bad_sequence", 400, "none");
        if (!DateTimeOffset.TryParse(envelope.GetProperty("issued_at").GetString(), out var issuedAt) ||
            !DateTimeOffset.TryParse(envelope.GetProperty("expires_at").GetString(), out var expiresAt))
            return Err("bad_time", 400, "none");
        var now = DateTimeOffset.UtcNow;
        if (issuedAt > now.AddSeconds(MaxClockSkewSeconds))
            return Err("clock_skew", 400, "none");
        var ttl = expiresAt - issuedAt;
        if (ttl <= TimeSpan.Zero || ttl > TimeSpan.FromSeconds(MaxTtlSeconds))
            return Err("bad_ttl", 400, "none");
        if (expiresAt <= now)
            return Err("command_expired", 410, "none");

        // ---- authorization: token scope, device, capability ----
        var tokenScopes = User.FindAll("scope")
            .SelectMany(c => c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (!tokenScopes.Contains(requiredScope))
            return Err("insufficient_scope", 403, "none");
        var device = await _db.Devices.FindAsync(deviceId);
        if (device is null || device.OwnerId != owner)
            return Err("unknown_device", 404, "none");
        if (device.Status != "Active")
            return Err("device_revoked", 409, "none");
        var capabilities = device.Capabilities.Split(',', StringSplitOptions.RemoveEmptyEntries);
        if (!capabilities.Contains(requiredScope))
            return Err("capability_not_granted", 403, "none");

        // ---- idempotency (stored receipt first) ----
        var binding = BindingHash(envelope);
        var existing = await _db.CommandRecords.FindAsync(commandId.ToString());
        if (existing is not null)
        {
            if (existing.BindingHash != binding)
                return Err("idempotency_conflict", 409, "query_status");
            var replayedResult = existing.ResultJson is null
                ? (JsonElement?)null
                : JsonDocument.Parse(existing.ResultJson).RootElement.Clone();
            return Ok(new
            {
                command_id = existing.CommandId,
                status = existing.Status,
                result = replayedResult,
                replayed = true,
            });
        }

        // ---- lease + epoch + sequence + local state ----
        var leaseId = envelope.GetProperty("lease_id").GetString()!;
        var (lease, leaseError) = await _leases.ValidateForCommandAsync(leaseId, controllerId, deviceId);
        if (leaseError is not null)
            return new ObjectResult(new
            {
                error = new
                {
                    code = leaseError.Code,
                    message = leaseError.Code,
                    next_action = leaseError.NextAction,
                },
            })
            { StatusCode = leaseError.HttpStatus };
        if (lease!.Epoch != epoch)
            return Err("stale_lease", 409, "reconnect");
        if (lease.NextSequence != sequence)
            return Err("stale_sequence", 409, "query_status");
        // Write operations need a write-mode lease: an observe lease
        // (or a consent that only approved observation) can never
        // input, even when the caller's token carries desktop.input.
        if (requiredScope == "desktop.input" && lease.Mode != "write")
            return Err("lease_mode", 403, "reauthorize");

        // ---- dispatch ----
        var commandBody = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            operation,
            payload = envelope.GetProperty("payload"),
            observation_id = envelope.TryGetProperty("observation_id", out var obs)
                ? obs.GetString() : null,
        })).RootElement.Clone();
        var outcome = await _dispatcher.DispatchAsync(deviceId, commandBody,
            ttl: expiresAt - DateTimeOffset.UtcNow);
        string status;
        JsonElement? result;
        if (!outcome.Delivered)
        {
            if (outcome.Error == "device_offline")
                return Err("device_offline", 503, "reconnect");
            if (outcome.Error == "too_many_pending")
                return Err("too_many_pending", 429, "none");
            return Err(outcome.Error ?? "dispatch_failed", 502, "none");
        }
        if (outcome.Error == "command_timeout")
        {
            status = "uncertain";
            result = null;
        }
        else if (outcome.Error is not null)
        {
            return Err(outcome.Error, 502, "none");
        }
        else
        {
            result = outcome.Result;
            status = result is { } r && r.TryGetProperty("status", out var s)
                ? s.GetString() ?? "succeeded"
                : "succeeded";
        }

        // ---- record receipt + advance sequence, atomically ----
        var record = new CommandRecord
        {
            CommandId = commandId.ToString(),
            DeviceId = deviceId,
            LeaseId = leaseId,
            Epoch = epoch,
            Sequence = sequence,
            Operation = operation,
            BindingHash = binding,
            Status = status,
            ResultJson = result is null ? null : result.Value.GetRawText(),
        };
        _db.CommandRecords.Add(record);
        lease.NextSequence = sequence + 1;
        await _db.SaveChangesAsync();

        return status == "uncertain"
            ? StatusCode(504, new { command_id = commandId, status, outcome = "uncertain" })
            : Ok(new { command_id = commandId, status, result });
    }

    /// <summary>Query a command's stored status (no input side effects).</summary>
    [HttpGet("/api/v1/commands/{commandId}")]
    public async Task<IActionResult> Get(string commandId)
    {
        var owner = User.FindFirst("sub")?.Value;
        if (owner is null)
            return Unauthorized();
        var record = await _db.CommandRecords.FindAsync(commandId);
        if (record is null)
            return Err("unknown_command", 404, "none");
        var device = await _db.Devices.FindAsync(record.DeviceId);
        if (device is null || device.OwnerId != owner)
            return Err("unknown_command", 404, "none");
        return Ok(new
        {
            command_id = record.CommandId,
            status = record.Status,
            result = record.ResultJson is null
                ? (JsonElement?)null
                : JsonDocument.Parse(record.ResultJson).RootElement.Clone(),
        });
    }

    public static string BindingHash(JsonElement envelope)
    {
        // Canonical binding: the envelope as sent, minus nothing.
        // JSON is compared byte-stable as received (the client sends
        // canonical key order; the server hashes the raw text).
        var raw = envelope.GetRawText();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))
            .ToLowerInvariant();
    }
}
