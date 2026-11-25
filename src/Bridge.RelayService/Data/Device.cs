using System.ComponentModel.DataAnnotations;

namespace Bridge.RelayService.Data;

/// <summary>
/// A bound device (T10). The server issues device_id; a user-supplied
/// id can never overwrite an existing device. The server stores the
/// public key only — the private key never leaves the device.
/// </summary>
public sealed class Device
{
    [Key]
    public string Id { get; set; } = Guid.NewGuid().ToString("D");

    /// <summary>Identity user id of the owner.</summary>
    public required string OwnerId { get; set; }

    public required string Alias { get; set; }

    /// <summary>DER-encoded P-256 public key.</summary>
    public required byte[] PublicKeyDer { get; set; }

    /// <summary>SHA-256 of the public key, lowercase hex.</summary>
    public required string KeyFingerprint { get; set; }

    /// <summary>Hash of (machine GUID + key fingerprint), lowercase hex.</summary>
    public required string MachineFingerprint { get; set; }

    /// <summary>Granted capabilities, comma-separated scopes.</summary>
    public required string Capabilities { get; set; }

    /// <summary>Active or Revoked.</summary>
    public string Status { get; set; } = "Active";

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? RevokedAt { get; set; }
}

/// <summary>
/// One pairing attempt (T10). The device initiates with a random
/// identity; the user claims it in the browser; the device completes
/// it with a high-entropy initiator secret. The user code alone is
/// never enough to complete a pairing.
/// </summary>
public sealed class DevicePairing
{
    [Key]
    public string Id { get; set; } = Guid.NewGuid().ToString("D");

    /// <summary>8 unambiguous chars, unique while pending.</summary>
    public required string UserCode { get; set; }

    /// <summary>SHA-256 hex of the initiator secret (never the secret).</summary>
    public required string InitiatorSecretHash { get; set; }

    public required string DeviceAlias { get; set; }
    public required string Capabilities { get; set; }
    public required string MachineFingerprint { get; set; }
    public required byte[] PublicKeyDer { get; set; }
    public required string KeyFingerprint { get; set; }

    /// <summary>Pending, Claimed, Completed, Expired, Cancelled.</summary>
    public string Status { get; set; } = "Pending";

    /// <summary>Identity user id that claimed it (null until claimed).</summary>
    public string? OwnerId { get; set; }

    /// <summary>Device id created at completion (null until completed).</summary>
    public string? DeviceId { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ClaimedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    // NOTE: no EF [Timestamp] rowversion here — SQLite's rowversion
    // does not give reliable optimistic concurrency. The claim and
    // complete transitions are atomic conditional UPDATEs instead
    // (see PairingController), which is what enforces single-winner.
}
