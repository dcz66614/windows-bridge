using System.ComponentModel.DataAnnotations;

namespace Bridge.RelayService.Data;

/// <summary>
/// The user's long-lived approval for a controller to drive a device
/// (T12). Default 2 h, max explicit 8 h. Created by the control
/// client; approved (or denied) locally by the device user — the
/// server never approves on the user's behalf. A consent is NOT a
/// lease: it only makes a lease acquirable.
/// </summary>
public sealed class ConsentSession
{
    [Key]
    public string Id { get; set; } = Guid.NewGuid().ToString("D");
    public required string DeviceId { get; set; }
    public required string OwnerId { get; set; }

    /// <summary>Controller identity: the OAuth client_id + subject.</summary>
    public required string ControllerId { get; set; }

    /// <summary>Requested scopes, comma-separated.</summary>
    public required string Scopes { get; set; }

    /// <summary>Pending, Approved, Denied, Expired, Revoked.</summary>
    public string Status { get; set; } = "Pending";

    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? DecidedAt { get; set; }
}

/// <summary>
/// A short control lease (T12). One WRITE lease per device per
/// interactive session; observe leases are shared. Liveness is a
/// MONOTONIC deadline (Stopwatch ticks) so a wall-clock rollback
/// cannot extend it; ExpiresAt is the wall-clock view for display.
/// </summary>
public sealed class ControlLease
{
    [Key]
    public string Id { get; set; } = Guid.NewGuid().ToString("D");
    public required string DeviceId { get; set; }
    public required string ConsentId { get; set; }
    public required string ControllerId { get; set; }

    /// <summary>"write" (exclusive) or "observe" (shared).</summary>
    public required string Mode { get; set; }

    /// <summary>Device connection epoch at acquire time.</summary>
    public int Epoch { get; set; }

    /// <summary>Next expected command sequence (starts at 1).</summary>
    public int NextSequence { get; set; } = 1;

    /// <summary>Active, Released, Expired.</summary>
    public string Status { get; set; } = "Active";

    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>Stopwatch ticks of the liveness deadline.</summary>
    public long DeadlineTicks { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// One accepted command, for idempotency (T12): the same command_id
/// with the same binding returns the stored receipt; the same id
/// with a different binding is IDEMPOTENCY_CONFLICT.
/// </summary>
public sealed class CommandRecord
{
    [Key]
    public required string CommandId { get; set; }
    public required string DeviceId { get; set; }
    public required string LeaseId { get; set; }
    public int Epoch { get; set; }
    public int Sequence { get; set; }
    public required string Operation { get; set; }

    /// <summary>SHA-256 of the canonical binding.</summary>
    public required string BindingHash { get; set; }

    /// <summary>succeeded, failed, uncertain.</summary>
    public required string Status { get; set; }

    /// <summary>Stored result JSON (or error code).</summary>
    public string? ResultJson { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
