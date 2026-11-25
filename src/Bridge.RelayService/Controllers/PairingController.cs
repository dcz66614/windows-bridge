using System.Security.Cryptography;
using System.Text;
using Bridge.RelayService.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Validation.AspNetCore;

namespace Bridge.RelayService.Controllers;

/// <summary>
/// Device pairing (T10). The device initiates with a random identity;
/// the user claims it in the browser; the device completes it with a
/// high-entropy initiator secret. The user code alone never completes
/// a pairing, and no failure leaves a half-bound controllable device.
/// </summary>
[ApiController]
public sealed class PairingController : ControllerBase
{
    /// <summary>Unambiguous alphabet: no 0/O, 1/I/L.</summary>
    public const string UserCodeAlphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
    public const int PairingLifetimeMinutes = 5;

    /// <summary>Capabilities a device may request at pairing time.
    /// files.workspace.write and developer.process need separate
    /// enablement and are refused here.</summary>
    public static readonly string[] AllowedCapabilities =
        { "device.connect", "desktop.observe", "desktop.input", "files.workspace.read" };

    private readonly ApplicationDbContext _db;
    private readonly PairingRateLimiter _limiter;
    private readonly UserManager<ApplicationUser> _users;

    public PairingController(
        ApplicationDbContext db,
        PairingRateLimiter limiter,
        UserManager<ApplicationUser> users)
    {
        _db = db;
        _limiter = limiter;
        _users = users;
    }

    public sealed record InitRequest(
        [property: System.Text.Json.Serialization.JsonPropertyName("device_alias")] string DeviceAlias,
        [property: System.Text.Json.Serialization.JsonPropertyName("capabilities")] string[] Capabilities,
        [property: System.Text.Json.Serialization.JsonPropertyName("machine_fingerprint")] string MachineFingerprint,
        [property: System.Text.Json.Serialization.JsonPropertyName("public_key_der_base64")] string PublicKeyDerBase64);

    [HttpPost("/api/v1/devices/pair/init")]
    [Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme,
        Policy = "DeviceConnect")]
    public async Task<IActionResult> Init([FromBody] InitRequest request)
    {
        var clientId = User.FindFirst("client_id")?.Value ?? "unknown";
        // Limited per (client, machine fingerprint): one physical
        // device may initiate at most 5 pairings/hour; a fleet of
        // distinct devices is not throttled by each other.
        if (!_limiter.TryConsume($"init:{clientId}:{request.MachineFingerprint}", 5, TimeSpan.FromHours(1)))
            return StatusCode(429, new { error = "rate_limited" });

        if (string.IsNullOrWhiteSpace(request.DeviceAlias) || request.DeviceAlias.Length > 64)
            return BadRequest(new { error = "invalid_alias" });
        if (request.Capabilities is null || request.Capabilities.Length == 0 ||
            request.Capabilities.Any(c => !AllowedCapabilities.Contains(c)))
            return BadRequest(new { error = "invalid_capabilities" });
        if (request.MachineFingerprint is null || request.MachineFingerprint.Length != 64 ||
            !request.MachineFingerprint.All(c => "0123456789abcdef".Contains(c)))
            return BadRequest(new { error = "invalid_machine_fingerprint" });
        byte[] publicKey;
        try
        {
            publicKey = Convert.FromBase64String(request.PublicKeyDerBase64);
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(publicKey, out _);
        }
        catch
        {
            return BadRequest(new { error = "invalid_public_key" });
        }

        var userCode = GenerateUserCode();
        var secret = RandomNumberGenerator.GetBytes(32);
        var pairing = new DevicePairing
        {
            UserCode = userCode,
            InitiatorSecretHash = Sha256Hex(secret),
            DeviceAlias = request.DeviceAlias,
            Capabilities = string.Join(",", request.Capabilities),
            MachineFingerprint = request.MachineFingerprint,
            PublicKeyDer = publicKey,
            KeyFingerprint = Sha256Hex(publicKey),
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(PairingLifetimeMinutes),
        };
        _db.DevicePairings.Add(pairing);
        await _db.SaveChangesAsync();

        var baseUrl = $"{Request.Scheme}://{Request.Host}";
        return Ok(new
        {
            pairing_id = pairing.Id,
            user_code = userCode,
            verification_uri = baseUrl + "/devices/pair",
            verification_uri_complete = baseUrl + "/devices/pair?code=" + userCode,
            expires_in = PairingLifetimeMinutes * 60,
            // The initiator secret is shown ONCE to the device that
            // created the pairing. It is never stored, logged, or
            // returned again.
            initiator_secret = Base64Url(secret),
        });
    }

    /// <summary>Pairing page: shows account, alias, capabilities and
    /// verification info before the user confirms.</summary>
    [HttpGet("/devices/pair")]
    [Authorize]
    public async Task<IActionResult> PairPage([FromQuery] string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return Content(PairHtml(null, "Enter the 8-character code from the device."), "text/html");
        var pairing = await _db.DevicePairings
            .FirstOrDefaultAsync(p => p.UserCode == code);
        if (pairing is null || pairing.Status != "Pending" || pairing.ExpiresAt <= DateTimeOffset.UtcNow)
            return Content(PairHtml(null, "This pairing code is unknown, expired, or already used."), "text/html");
        var user = await _users.GetUserAsync(User);
        return Content(PairHtml((pairing, user?.Email ?? user?.UserName ?? "?"), null), "text/html");
    }

    [HttpPost("/devices/pair/confirm")]
    [Authorize]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Confirm([FromForm] string user_code, [FromForm] string decision)
    {
        var user = await _users.GetUserAsync(User);
        if (user is null)
            return Challenge();
        if (!_limiter.TryConsume($"confirm:{user_code}", 10, TimeSpan.FromMinutes(10)))
            return StatusCode(429, "Too many attempts for this pairing code.");
        var pairing = await _db.DevicePairings.FirstOrDefaultAsync(p => p.UserCode == user_code);
        if (pairing is null)
            return Content(PairHtml(null, "This pairing code is unknown."), "text/html");
        if (pairing.Status != "Pending" || pairing.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            if (pairing.Status == "Pending")
            {
                pairing.Status = "Expired";
                await _db.SaveChangesAsync();
            }
            return Content(PairHtml(null, "This pairing code is expired or already used."), "text/html");
        }
        if (decision != "confirm")
        {
            pairing.Status = "Cancelled";
            await _db.SaveChangesAsync();
            return Content(PairHtml(null, "Pairing cancelled. No device was bound."), "text/html");
        }
        // Atomic claim: the UPDATE only succeeds while the pairing is
        // still Pending. Two simultaneous claims: exactly one wins
        // (SQLite has no reliable EF rowversion under concurrency).
        var claimed = await _db.Database.ExecuteSqlAsync(
            $"UPDATE DevicePairings SET Status = 'Claimed', OwnerId = {user.Id}, ClaimedAt = {DateTimeOffset.UtcNow} WHERE Id = {pairing.Id} AND Status = 'Pending'");
        if (claimed == 0)
            return Content(PairHtml(null, "This pairing was already claimed."), "text/html");
        return Content(PairHtml((pairing, user.Email ?? user.UserName ?? "?"),
            "Pairing claimed. The device will now complete the binding."), "text/html");
    }

    [HttpPost("/api/v1/devices/pair/complete")]
    [Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme,
        Policy = "DeviceConnect")]
    public async Task<IActionResult> Complete([FromBody] CompleteRequest request)
    {
        if (!_limiter.TryConsume($"complete:{request.PairingId}", 10, TimeSpan.FromMinutes(10)))
            return StatusCode(429, new { error = "rate_limited" });
        var pairing = await _db.DevicePairings.FindAsync(request.PairingId);
        if (pairing is null)
            return BadRequest(new { error = "unknown_pairing" });
        if (pairing.Status == "Completed")
            return BadRequest(new { error = "already_redeemed" });
        if (pairing.Status == "Cancelled")
            return BadRequest(new { error = "cancelled" });
        if (pairing.Status != "Claimed" || pairing.OwnerId is null)
            return BadRequest(new { error = "not_claimed" });
        if (pairing.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            pairing.Status = "Expired";
            await _db.SaveChangesAsync();
            return BadRequest(new { error = "expired" });
        }
        if (!FixedTimeEquals(Sha256Hex(Base64UrlDecode(request.InitiatorSecret)), pairing.InitiatorSecretHash))
            return BadRequest(new { error = "bad_initiator_secret" });

        // Device creation + pairing completion are one transaction.
        // The conditional UPDATE makes duplicate redemption atomic:
        // only the first complete flips Claimed -> Completed.
        await using var tx = await _db.Database.BeginTransactionAsync();
        // Clone protection, checked inside the transaction (SQLite
        // serializes writers): the same key on a different machine
        // while the old device is still active means a copied identity.
        var clash = await _db.Devices.FirstOrDefaultAsync(d =>
            d.KeyFingerprint == pairing.KeyFingerprint && d.Status == "Active");
        if (clash is not null && clash.MachineFingerprint != pairing.MachineFingerprint)
            return BadRequest(new { error = "key_in_use_on_another_machine" });
        if (clash is not null)
            return BadRequest(new { error = "already_bound" });

        var device = new Device
        {
            OwnerId = pairing.OwnerId,
            Alias = pairing.DeviceAlias,
            PublicKeyDer = pairing.PublicKeyDer,
            KeyFingerprint = pairing.KeyFingerprint,
            MachineFingerprint = pairing.MachineFingerprint,
            Capabilities = pairing.Capabilities,
        };
        _db.Devices.Add(device);
        await _db.SaveChangesAsync();
        var completed = await _db.Database.ExecuteSqlAsync(
            $"UPDATE DevicePairings SET Status = 'Completed', DeviceId = {device.Id}, CompletedAt = {DateTimeOffset.UtcNow} WHERE Id = {pairing.Id} AND Status = 'Claimed'");
        if (completed == 0)
        {
            // Someone else completed (or cancelled) it meanwhile:
            // roll the device back — no half-bound device survives.
            await tx.RollbackAsync();
            return BadRequest(new { error = "already_redeemed" });
        }
        await tx.CommitAsync();

        // The device learns its owner here; the local UI shows the
        // account so a wrong-account claim is visible immediately.
        return Ok(new
        {
            device_id = device.Id,
            owner_id = device.OwnerId,
            alias = device.Alias,
        });
    }

    public sealed record CompleteRequest(
        [property: System.Text.Json.Serialization.JsonPropertyName("pairing_id")] string PairingId,
        [property: System.Text.Json.Serialization.JsonPropertyName("initiator_secret")] string InitiatorSecret);

    [HttpPost("/api/v1/devices/pair/cancel")]
    [Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme,
        Policy = "DeviceConnect")]
    public async Task<IActionResult> Cancel([FromBody] CancelRequest request)
    {
        var pairing = await _db.DevicePairings.FindAsync(request.PairingId);
        if (pairing is null)
            return BadRequest(new { error = "unknown_pairing" });
        if (pairing.Status is "Pending" or "Claimed")
        {
            pairing.Status = "Cancelled";
            await _db.SaveChangesAsync();
        }
        return Ok(new { status = pairing.Status });
    }

    public sealed record CancelRequest(
        [property: System.Text.Json.Serialization.JsonPropertyName("pairing_id")] string PairingId);

    // ---- helpers ----

    public static string GenerateUserCode()
    {
        var bytes = RandomNumberGenerator.GetBytes(8);
        var chars = new char[8];
        for (var i = 0; i < 8; i++)
            chars[i] = UserCodeAlphabet[bytes[i] % UserCodeAlphabet.Length];
        return new string(chars);
    }

    public static string Sha256Hex(byte[] data) =>
        Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    public static string Base64Url(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] Base64UrlDecode(string value)
    {
        var s = value.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }
        return Convert.FromBase64String(s);
    }

    private static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    private string PairHtml((DevicePairing Pairing, string Account)? info, string? message)
    {
        var msg = message is null ? "" :
            $"<p><b>{System.Net.WebUtility.HtmlEncode(message)}</b></p>";
        if (info is null)
            return $"<!doctype html><html><body><h1>Pair a device</h1>{msg}" +
                "<form method=\"get\" action=\"/devices/pair\">" +
                "<label>Code: <input name=\"code\" maxlength=\"8\"></label>" +
                "<button type=\"submit\">Look up</button></form></body></html>";
        var (pairing, account) = info.Value;
        var tokens = HttpContext.RequestServices
            .GetRequiredService<Microsoft.AspNetCore.Antiforgery.IAntiforgery>()
            .GetAndStoreTokens(HttpContext);
        return "<!doctype html><html><body><h1>Pair a device</h1>" + msg +
            $"<p>Account: <b>{System.Net.WebUtility.HtmlEncode(account)}</b></p>" +
            $"<p>Device alias: <b>{System.Net.WebUtility.HtmlEncode(pairing.DeviceAlias)}</b></p>" +
            $"<p>Requested capabilities: <b>{System.Net.WebUtility.HtmlEncode(pairing.Capabilities)}</b></p>" +
            $"<p>Code: <b>{System.Net.WebUtility.HtmlEncode(pairing.UserCode)}</b></p>" +
            $"<p>Key fingerprint: <code>{pairing.KeyFingerprint[..16]}…</code></p>" +
            $"<p>Machine fingerprint: <code>{pairing.MachineFingerprint[..16]}…</code></p>" +
            $"<p>Expires: {pairing.ExpiresAt:u}</p>" +
            "<form method=\"post\" action=\"/devices/pair/confirm\">" +
            $"<input type=\"hidden\" name=\"__RequestVerificationToken\" value=\"{tokens.RequestToken}\">" +
            $"<input type=\"hidden\" name=\"user_code\" value=\"{pairing.UserCode}\">" +
            "<button type=\"submit\" name=\"decision\" value=\"confirm\">Confirm pairing</button> " +
            "<button type=\"submit\" name=\"decision\" value=\"cancel\">Cancel</button>" +
            "</form></body></html>";
    }
}
