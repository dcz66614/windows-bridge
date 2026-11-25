using Bridge.RelayService.Data;
using Bridge.RelayService.DeviceChannel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Validation.AspNetCore;

namespace Bridge.RelayService.Controllers;

/// <summary>
/// Consent sessions and control leases (T12). The control client
/// asks; the device user approves locally; only then can a lease
/// be acquired. Releasing a lease never revokes the binding.
/// </summary>
[ApiController]
[Route("api/v1")]
[Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme)]
public sealed class LeasesController : ControllerBase
{
    private readonly ApplicationDbContext _db;
    private readonly LeaseManager _leases;

    public LeasesController(ApplicationDbContext db, LeaseManager leases)
    {
        _db = db;
        _leases = leases;
    }

    private string ControllerId =>
        (User.FindFirst("client_id")?.Value ?? "unknown") + ":" +
        (User.FindFirst("sub")?.Value ?? "unknown");

    private static object Error(LeaseManager.LeaseError e, string? detail = null) => new
    {
        error = new
        {
            code = e.Code,
            message = detail ?? e.Code,
            next_action = e.NextAction,
        },
    };

    public sealed record CreateConsentRequest(
        [property: System.Text.Json.Serialization.JsonPropertyName("device_id")] string DeviceId,
        [property: System.Text.Json.Serialization.JsonPropertyName("scopes")] string[] Scopes,
        [property: System.Text.Json.Serialization.JsonPropertyName("duration_seconds")] int? DurationSeconds);

    /// <summary>
    /// Ask for local approval. Returns 202 while the device user
    /// decides; the device gets a lease_update frame. Nothing is
    /// approved automatically, and a pause can never be lifted
    /// remotely.
    /// </summary>
    [HttpPost("leases")]
    [Authorize(Policy = "DevicesRead")]
    public async Task<IActionResult> Create([FromBody] CreateConsentRequest request)
    {
        var owner = User.FindFirst("sub")?.Value;
        if (owner is null)
            return Unauthorized();
        var device = await _db.Devices.FindAsync(request.DeviceId);
        if (device is null || device.OwnerId != owner)
            return NotFound(new { error = new { code = "unknown_device", message = "unknown_device", next_action = "none" } });
        if (device.Status != "Active")
            return StatusCode(409, new { error = new { code = "device_revoked", message = "device_revoked", next_action = "none" } });
        if (request.Scopes is null || request.Scopes.Length == 0 ||
            request.Scopes.Any(s => s is not ("desktop.observe" or "desktop.input" or "device.connect")))
            return BadRequest(new { error = new { code = "invalid_scopes", message = "invalid_scopes", next_action = "none" } });
        var tokenScopes = User.FindAll("scope")
            .SelectMany(c => c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (request.Scopes.Any(s => !tokenScopes.Contains(s)))
            return StatusCode(403, new { error = new { code = "insufficient_scope", message = "insufficient_scope", next_action = "none" } });
        var duration = request.DurationSeconds ?? LeaseManager.DefaultConsentSeconds;
        if (duration < LeaseManager.MinConsentSeconds || duration > LeaseManager.MaxConsentSeconds)
            return BadRequest(new { error = new { code = "invalid_duration", message = "invalid_duration", next_action = "none" } });

        var consent = await _leases.CreateConsentAsync(
            request.DeviceId, owner, ControllerId,
            string.Join(",", request.Scopes), duration);
        return StatusCode(202, new
        {
            consent_id = consent.Id,
            status = consent.Status,
            device_id = consent.DeviceId,
            scopes = request.Scopes,
            expires_at = consent.ExpiresAt,
        });
    }

    /// <summary>Local decision, from the DEVICE token (device.connect).</summary>
    [HttpPost("devices/{deviceId}/consent/{consentId}/decide")]
    [Authorize(Policy = "DeviceConnect")]
    public async Task<IActionResult> Decide(
        string deviceId, string consentId, [FromBody] DecideRequest request)
    {
        var owner = User.FindFirst("sub")?.Value;
        if (owner is null)
            return Unauthorized();
        var device = await _db.Devices.FindAsync(deviceId);
        if (device is null || device.OwnerId != owner)
            return NotFound(new { error = new { code = "unknown_device", message = "unknown_device", next_action = "none" } });
        var ok = await _leases.DecideConsentAsync(consentId, deviceId, request.Approved);
        if (!ok)
            return NotFound(new { error = new { code = "unknown_consent", message = "unknown_consent", next_action = "none" } });
        return Ok(new { consent_id = consentId, status = request.Approved ? "Approved" : "Denied" });
    }

    public sealed record DecideRequest(
        [property: System.Text.Json.Serialization.JsonPropertyName("approved")] bool Approved);

    public sealed record AcquireRequest(
        [property: System.Text.Json.Serialization.JsonPropertyName("mode")] string Mode,
        [property: System.Text.Json.Serialization.JsonPropertyName("ttl_seconds")] int? TtlSeconds);

    /// <summary>Acquire a lease under an approved consent.</summary>
    [HttpPost("leases/{consentId}/acquire")]
    public async Task<IActionResult> Acquire(string consentId, [FromBody] AcquireRequest request)
    {
        if (request.Mode is not ("write" or "observe"))
            return BadRequest(new { error = new { code = "invalid_mode", message = "invalid_mode", next_action = "none" } });
        var ttl = request.TtlSeconds ?? LeaseManager.DefaultLeaseSeconds;
        if (ttl < LeaseManager.MinLeaseSeconds || ttl > LeaseManager.MaxLeaseSeconds)
            return BadRequest(new { error = new { code = "invalid_ttl", message = "invalid_ttl", next_action = "none" } });
        var result = await _leases.AcquireAsync(consentId, ControllerId, request.Mode, ttl);
        if (result.Error is not null)
        {
            var status = result.Error.HttpStatus;
            if (result.Error.Code == "lease_held")
                return StatusCode(409, Error(result.Error, result.HolderInfo));
            return StatusCode(status, Error(result.Error));
        }
        var lease = result.Lease!;
        return Ok(new
        {
            lease_id = lease.Id,
            device_id = lease.DeviceId,
            mode = lease.Mode,
            epoch = lease.Epoch,
            next_sequence = lease.NextSequence,
            expires_at = lease.ExpiresAt,
        });
    }

    public sealed record RenewRequest(
        [property: System.Text.Json.Serialization.JsonPropertyName("ttl_seconds")] int? TtlSeconds);

    [HttpPost("leases/{leaseId}/renew")]
    public async Task<IActionResult> Renew(string leaseId, [FromBody] RenewRequest request)
    {
        var ttl = request.TtlSeconds ?? LeaseManager.DefaultLeaseSeconds;
        if (ttl < LeaseManager.MinLeaseSeconds || ttl > LeaseManager.MaxLeaseSeconds)
            return BadRequest(new { error = new { code = "invalid_ttl", message = "invalid_ttl", next_action = "none" } });
        var result = await _leases.RenewAsync(leaseId, ControllerId, ttl);
        if (!result.Ok)
            return StatusCode(result.Error!.HttpStatus, Error(result.Error));
        var lease = await _db.ControlLeases.FindAsync(leaseId);
        return Ok(new { lease_id = leaseId, expires_at = lease!.ExpiresAt });
    }

    /// <summary>Release a lease. Does not revoke the binding.</summary>
    [HttpDelete("leases/{leaseId}")]
    public async Task<IActionResult> Release(string leaseId)
    {
        var ok = await _leases.ReleaseAsync(leaseId, ControllerId);
        if (!ok)
            return NotFound(new { error = new { code = "unknown_lease", message = "unknown_lease", next_action = "none" } });
        return Ok(new { lease_id = leaseId, status = "Released" });
    }

    [HttpGet("leases/{leaseId}")]
    public async Task<IActionResult> Get(string leaseId)
    {
        var lease = await _db.ControlLeases.FindAsync(leaseId);
        if (lease is null || lease.ControllerId != ControllerId)
            return NotFound(new { error = new { code = "unknown_lease", message = "unknown_lease", next_action = "none" } });
        return Ok(new
        {
            lease_id = lease.Id,
            device_id = lease.DeviceId,
            mode = lease.Mode,
            epoch = lease.Epoch,
            next_sequence = lease.NextSequence,
            status = lease.Status,
            expires_at = lease.ExpiresAt,
        });
    }
}
