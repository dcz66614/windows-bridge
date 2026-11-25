using System.Diagnostics;
using Bridge.RelayService.Data;
using Microsoft.EntityFrameworkCore;

namespace Bridge.RelayService.DeviceChannel;

/// <summary>
/// Consent sessions and control leases (T12). Consent is the user's
/// long approval (2 h default, 8 h max); a lease is the short
/// liveness grant (60 s default) that makes one command stream
/// possible. One WRITE lease per device; observe leases are shared.
/// </summary>
public sealed class LeaseManager
{
    public const int DefaultConsentSeconds = 2 * 60 * 60;
    public const int MaxConsentSeconds = 8 * 60 * 60;
    public const int MinConsentSeconds = 60;
    public const int DefaultLeaseSeconds = 60;
    public const int MinLeaseSeconds = 5;
    public const int MaxLeaseSeconds = 60;

    private readonly ApplicationDbContext _db;
    private readonly DeviceConnectionRegistry _registry;

    public LeaseManager(ApplicationDbContext db, DeviceConnectionRegistry registry)
    {
        _db = db;
        _registry = registry;
    }

    /// <summary>Monotonic now, in Stopwatch ticks.</summary>
    public static long NowTicks => Stopwatch.GetTimestamp();

    public static long DeadlineTicks(TimeSpan fromNow) =>
        NowTicks + (long)(fromNow.TotalSeconds * Stopwatch.Frequency);

    public static bool IsLive(long deadlineTicks) => NowTicks < deadlineTicks;

    public sealed record LeaseError(string Code, int HttpStatus, string NextAction);
    public sealed record AcquireResult(ControlLease? Lease, LeaseError? Error, string? HolderInfo);

    public async Task<ConsentSession> CreateConsentAsync(
        string deviceId, string ownerId, string controllerId, string scopes,
        int durationSeconds)
    {
        var consent = new ConsentSession
        {
            DeviceId = deviceId,
            OwnerId = ownerId,
            ControllerId = controllerId,
            Scopes = scopes,
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(durationSeconds),
        };
        _db.ConsentSessions.Add(consent);
        await _db.SaveChangesAsync();
        // Notify the device: the local user must approve.
        await NotifyApprovalRequestedAsync(deviceId, consent);
        return consent;
    }

    public async Task<bool> DecideConsentAsync(
        string consentId, string deviceId, bool approved)
    {
        var consent = await _db.ConsentSessions.FindAsync(consentId);
        if (consent is null || consent.DeviceId != deviceId || consent.Status != "Pending")
            return false;
        consent.Status = approved ? "Approved" : "Denied";
        consent.DecidedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<AcquireResult> AcquireAsync(
        string consentId, string controllerId, string mode, int ttlSeconds)
    {
        var consent = await _db.ConsentSessions.FindAsync(consentId);
        if (consent is null || consent.ControllerId != controllerId)
            return new AcquireResult(null,
                new LeaseError("unknown_consent", 404, "none"), null);
        if (consent.Status == "Pending")
            return new AcquireResult(null,
                new LeaseError("consent_pending", 409, "observe"), null);
        if (consent.Status == "Denied")
            return new AcquireResult(null,
                new LeaseError("consent_denied", 409, "reauthorize"), null);
        if (consent.Status != "Approved" || consent.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            if (consent.Status == "Approved")
            {
                consent.Status = "Expired";
                await _db.SaveChangesAsync();
            }
            return new AcquireResult(null,
                new LeaseError("consent_expired", 409, "reauthorize"), null);
        }
        var connection = _registry.Get(consent.DeviceId);
        if (connection is null)
            return new AcquireResult(null,
                new LeaseError("device_offline", 503, "reconnect"), null);

        // The mode must be covered by what the device user approved.
        var consentScopes = consent.Scopes.Split(',',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var neededScope = mode == "write" ? "desktop.input" : "desktop.observe";
        if (!consentScopes.Contains(neededScope))
            return new AcquireResult(null,
                new LeaseError("consent_scope", 409, "reauthorize"),
                $"Consent covers {consent.Scopes}; {mode} needs {neededScope}.");

        if (mode == "write")
        {
            // SQLite cannot ORDER BY DateTimeOffset in SQL; sort in memory.
            var held = (await _db.ControlLeases
                .Where(l => l.DeviceId == consent.DeviceId &&
                            l.Mode == "write" && l.Status == "Active")
                .ToListAsync())
                .OrderByDescending(l => l.CreatedAt)
                .FirstOrDefault();
            if (held is not null && IsLive(held.DeadlineTicks))
                return new AcquireResult(null,
                    new LeaseError("lease_held", 409, "none"),
                    $"Held by {held.ControllerId} until {held.ExpiresAt:u}.");
            if (held is not null)
            {
                held.Status = "Expired";
                await _db.SaveChangesAsync();
            }
        }

        var lease = new ControlLease
        {
            DeviceId = consent.DeviceId,
            ConsentId = consent.Id,
            ControllerId = controllerId,
            Mode = mode,
            Epoch = connection.Epoch,
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(ttlSeconds),
            DeadlineTicks = DeadlineTicks(TimeSpan.FromSeconds(ttlSeconds)),
        };
        _db.ControlLeases.Add(lease);
        await _db.SaveChangesAsync();
        return new AcquireResult(lease, null, null);
    }

    public sealed record RenewResult(bool Ok, LeaseError? Error);

    public async Task<RenewResult> RenewAsync(
        string leaseId, string controllerId, int ttlSeconds)
    {
        var lease = await _db.ControlLeases.FindAsync(leaseId);
        if (lease is null || lease.ControllerId != controllerId)
            return new RenewResult(false, new LeaseError("unknown_lease", 404, "none"));
        if (lease.Status != "Active" || !IsLive(lease.DeadlineTicks))
        {
            lease.Status = "Expired";
            await _db.SaveChangesAsync();
            return new RenewResult(false, new LeaseError("lease_expired", 409, "reauthorize"));
        }
        var consent = await _db.ConsentSessions.FindAsync(lease.ConsentId);
        if (consent is null || consent.Status != "Approved" ||
            consent.ExpiresAt <= DateTimeOffset.UtcNow)
            return new RenewResult(false, new LeaseError("consent_expired", 409, "reauthorize"));
        var connection = _registry.Get(lease.DeviceId);
        if (connection is null)
            return new RenewResult(false, new LeaseError("device_offline", 503, "reconnect"));
        if (connection.Epoch != lease.Epoch)
        {
            lease.Status = "Expired";
            await _db.SaveChangesAsync();
            return new RenewResult(false, new LeaseError("stale_lease", 409, "reconnect"));
        }
        if (connection.Paused || connection.Locked)
            return new RenewResult(false, new LeaseError("local_paused", 423, "local_resume"));
        lease.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(ttlSeconds);
        lease.DeadlineTicks = DeadlineTicks(TimeSpan.FromSeconds(ttlSeconds));
        await _db.SaveChangesAsync();
        return new RenewResult(true, null);
    }

    public async Task<bool> ReleaseAsync(string leaseId, string controllerId)
    {
        var lease = await _db.ControlLeases.FindAsync(leaseId);
        if (lease is null || lease.ControllerId != controllerId)
            return false;
        if (lease.Status == "Active")
        {
            lease.Status = "Released";
            await _db.SaveChangesAsync();
        }
        return true;
    }

    /// <summary>
    /// Validate a lease for one command (T12). Returns the lease when
    /// it may execute, else the error to report.
    /// </summary>
    public async Task<(ControlLease? Lease, LeaseError? Error)> ValidateForCommandAsync(
        string leaseId, string controllerId, string deviceId)
    {
        var lease = await _db.ControlLeases.FindAsync(leaseId);
        if (lease is null || lease.ControllerId != controllerId || lease.DeviceId != deviceId)
            return (null, new LeaseError("unknown_lease", 404, "none"));
        if (lease.Status != "Active" || !IsLive(lease.DeadlineTicks))
        {
            if (lease.Status == "Active")
            {
                lease.Status = "Expired";
                await _db.SaveChangesAsync();
            }
            return (null, new LeaseError("lease_expired", 409, "reauthorize"));
        }
        var consent = await _db.ConsentSessions.FindAsync(lease.ConsentId);
        if (consent is null || consent.Status != "Approved" ||
            consent.ExpiresAt <= DateTimeOffset.UtcNow)
            return (null, new LeaseError("consent_expired", 409, "reauthorize"));
        var connection = _registry.Get(deviceId);
        if (connection is null)
            return (null, new LeaseError("device_offline", 503, "reconnect"));
        if (connection.Epoch != lease.Epoch)
            return (null, new LeaseError("stale_lease", 409, "reconnect"));
        if (connection.Paused)
            return (null, new LeaseError("local_paused", 423, "local_resume"));
        if (connection.Locked)
            return (null, new LeaseError("local_locked", 423, "local_resume"));
        return (lease, null);
    }

    private async Task NotifyApprovalRequestedAsync(string deviceId, ConsentSession consent)
    {
        var connection = _registry.Get(deviceId);
        if (connection is null)
            return;
        try
        {
            await Bridge.DeviceChannel.ChannelFraming.SendAsync(connection.Socket,
                Bridge.DeviceChannel.ChannelEnvelope.Create("lease_update", new
                {
                    action = "approval_requested",
                    consent_id = consent.Id,
                    controller_id = consent.ControllerId,
                    scopes = consent.Scopes,
                    expires_at = consent.ExpiresAt,
                }), CancellationToken.None);
        }
        catch
        {
            // The device will see the request on its next connection
            // (the consent stays Pending until decided).
        }
    }
}
