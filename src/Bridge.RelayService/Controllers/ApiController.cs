using Bridge.RelayService.Data;
using Bridge.RelayService.DeviceChannel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Validation.AspNetCore;

namespace Bridge.RelayService.Controllers;

/// <summary>
/// The caller's own device registry (T10): list + revoke. Revocation
/// marks the device; the WSS handshake (T11) is the enforcement point
/// — a revoked device is refused a new connection, and a live one is
/// evicted. The device's live access token (10 min TTL) is NOT
/// revoked here; that is a documented v1 limit.
/// </summary>
[ApiController]
[Route("api/v1")]
[Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme)]
public sealed class ApiController : ControllerBase
{
    private readonly ApplicationDbContext _db;
    private readonly DeviceConnectionRegistry _registry;

    public ApiController(ApplicationDbContext db, DeviceConnectionRegistry registry)
    {
        _db = db;
        _registry = registry;
    }

    [HttpGet("devices")]
    [Authorize(Policy = "DevicesRead")]
    public async Task<IActionResult> ListDevices()
    {
        var owner = User.FindFirst("sub")?.Value;
        if (owner is null)
            return Unauthorized();
        var devices = (await _db.Devices
            .Where(d => d.OwnerId == owner)
            .Select(d => new
            {
                device_id = d.Id,
                alias = d.Alias,
                capabilities = d.Capabilities,
                status = d.Status,
                key_fingerprint = d.KeyFingerprint,
                created_at = d.CreatedAt,
                revoked_at = d.RevokedAt,
            })
            .ToListAsync())
            // SQLite cannot ORDER BY DateTimeOffset in SQL; sort in memory.
            .OrderByDescending(d => d.created_at)
            .ToList();
        return Ok(new { devices });
    }

    /// <summary>Revoke (forget) a device. Only its owner may do so.</summary>
    [HttpDelete("devices/{deviceId}")]
    [Authorize(Policy = "DevicesRead")]
    public async Task<IActionResult> RevokeDevice(string deviceId)
    {
        var owner = User.FindFirst("sub")?.Value;
        if (owner is null)
            return Unauthorized();
        var device = await _db.Devices.FindAsync(deviceId);
        if (device is null || device.OwnerId != owner)
            return NotFound(new { error = "unknown_device" });
        if (device.Status != "Revoked")
        {
            device.Status = "Revoked";
            device.RevokedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync();
            // Revocation propagates to a live connection immediately:
            // the device gets a revoke frame and its socket closes.
            await _registry.EvictAsync(device.Id, "revoked");
        }
        return Ok(new { device_id = device.Id, status = device.Status });
    }
}
