using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Web;
using Bridge.DeviceIdentity;
using Bridge.RelayService.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Bridge.RelayService.Tests;

/// <summary>
/// T10 pairing acceptance: happy path + expired, wrong account,
/// concurrent claim, duplicate redemption, cancel, leaked code,
/// revocation, forget/reinstall, clone protection. Real service,
/// TestServer, no mocks.
/// </summary>
public sealed class PairingTests : IClassFixture<RelayFactory>
{
    private readonly RelayFactory _factory;
    private const string Password = "Correct-Horse-99!";

    private static string NewEmail() => "pair-" + Guid.NewGuid().ToString("N")[..8] + "@example.test";
    private static string NewFingerprint() =>
        MachineFingerprint.Compute(Guid.NewGuid().ToString("D"), "Windows 11");

    public PairingTests(RelayFactory factory) => _factory = factory;

    private static string Q(string url, string name)
    {
        var uri = new Uri(url);
        return HttpUtility.ParseQueryString(uri.Query)[name]
            ?? throw new InvalidOperationException($"Missing {name} in {url}.");
    }

    private async Task<(HttpClient Client, string Access, string UserId)> DeviceTokenAsync()
    {
        var email = NewEmail();
        var user = await TestHelpers.CreateUserAsync(_factory, email, Password);
        var client = TestHelpers.CreateClient(_factory);
        await TestHelpers.LoginAsync(client, email, Password);
        var (verifier, challenge) = TestHelpers.Pkce();
        var authUrl = "/connect/authorize?response_type=code"
            + "&client_id=windows-agent&redirect_uri=http://127.0.0.1/callback"
            + "&scope=device.connect%20offline_access"
            + $"&code_challenge={challenge}&code_challenge_method=S256&state=s1";
        using var authorized = await TestHelpers.SubmitConsentAsync(client, authUrl);
        var code = Q(authorized.Headers.Location!.ToString(), "code");
        using var token = await client.PostAsync("/connect/token", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = "http://127.0.0.1/callback",
                ["client_id"] = "windows-agent",
                ["code_verifier"] = verifier,
            }));
        var body = await token.Content.ReadAsStringAsync();
        Assert.True(token.IsSuccessStatusCode, $"Device token failed: {(int)token.StatusCode} {body}");
        using var doc = JsonDocument.Parse(body);
        return (client, doc.RootElement.GetProperty("access_token").GetString()!, user.Id);
    }

    private async Task<(HttpClient Client, string Access, string UserId)> ControlTokenAsync()
    {
        var email = NewEmail();
        var user = await TestHelpers.CreateUserAsync(_factory, email, Password);
        var client = TestHelpers.CreateClient(_factory);
        await TestHelpers.LoginAsync(client, email, Password);
        var (verifier, challenge) = TestHelpers.Pkce();
        var authUrl = "/connect/authorize?response_type=code"
            + "&client_id=muse-control&redirect_uri=http://127.0.0.1/callback"
            + "&scope=devices.read%20offline_access"
            + $"&code_challenge={challenge}&code_challenge_method=S256&state=s1";
        using var authorized = await TestHelpers.SubmitConsentAsync(client, authUrl);
        var code = Q(authorized.Headers.Location!.ToString(), "code");
        using var token = await client.PostAsync("/connect/token", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = "http://127.0.0.1/callback",
                ["client_id"] = "muse-control",
                ["code_verifier"] = verifier,
            }));
        var body = await token.Content.ReadAsStringAsync();
        Assert.True(token.IsSuccessStatusCode, $"Control token failed: {(int)token.StatusCode} {body}");
        using var doc = JsonDocument.Parse(body);
        return (client, doc.RootElement.GetProperty("access_token").GetString()!, user.Id);
    }

    private async Task<HttpClient> BrowserLoginAsync()
    {
        var email = NewEmail();
        var client = TestHelpers.CreateClient(_factory);
        await TestHelpers.CreateUserAsync(_factory, email, Password);
        await TestHelpers.LoginAsync(client, email, Password);
        return client;
    }

    private static HttpRequestMessage WithToken(HttpMethod method, string url, string access)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
        return request;
    }

    private async Task<JsonElement> InitPairingAsync(
        HttpClient client, string access, DeviceKeyPair key, string fingerprint,
        string alias = "SAM-PC", string[]? caps = null)
    {
        using var request = WithToken(HttpMethod.Post, "/api/v1/devices/pair/init", access);
        request.Content = JsonContent.Create(new
        {
            device_alias = alias,
            capabilities = caps ?? new[] { "device.connect", "desktop.observe", "desktop.input" },
            machine_fingerprint = fingerprint,
            public_key_der_base64 = Convert.ToBase64String(key.PublicKeyDer),
        });
        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode,
            $"Init failed: {(int)response.StatusCode} {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private async Task<(HttpStatusCode Status, string Body)> ConfirmAsync(
        HttpClient browser, string userCode, string decision = "confirm")
    {
        using var page = await browser.GetAsync("/devices/pair?code=" + Uri.EscapeDataString(userCode));
        var html = await page.Content.ReadAsStringAsync();
        var token = TestHelpers.AntiforgeryToken(html);
        using var response = await browser.PostAsync("/devices/pair/confirm",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["user_code"] = userCode,
                ["decision"] = decision,
            }));
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private async Task<(HttpStatusCode Status, string Body)> CompleteAsync(
        HttpClient client, string access, string pairingId, string secret)
    {
        using var request = WithToken(HttpMethod.Post, "/api/v1/devices/pair/complete", access);
        request.Content = JsonContent.Create(new
        {
            pairing_id = pairingId,
            initiator_secret = secret,
        });
        using var response = await client.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private async Task ExpirePairingAsync(string pairingId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var pairing = await db.DevicePairings.FindAsync(pairingId);
        Assert.NotNull(pairing);
        pairing!.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();
    }

    private async Task<JsonElement> ListDevicesAsync(HttpClient client, string access)
    {
        using var request = WithToken(HttpMethod.Get, "/api/v1/devices", access);
        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"List failed: {(int)response.StatusCode} {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    [Fact]
    public async Task HappyPath_Init_Claim_Complete_List()
    {
        var (deviceClient, deviceAccess, _) = await DeviceTokenAsync();
        var key = DeviceKeyPair.Generate();
        var fp = NewFingerprint();
        var init = await InitPairingAsync(deviceClient, deviceAccess, key, fp);
        var pairingId = init.GetProperty("pairing_id").GetString()!;
        var userCode = init.GetProperty("user_code").GetString()!;
        var secret = init.GetProperty("initiator_secret").GetString()!;
        Assert.Equal(8, userCode.Length);
        Assert.Equal(300, init.GetProperty("expires_in").GetInt32());

        var browser = await BrowserLoginAsync();
        using var page = await browser.GetAsync("/devices/pair?code=" + userCode);
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("SAM-PC", html);
        Assert.Contains("desktop.observe", html);
        Assert.Contains(userCode, html);

        var (confirmStatus, confirmBody) = await ConfirmAsync(browser, userCode);
        Assert.Equal(HttpStatusCode.OK, confirmStatus);
        Assert.Contains("claimed", confirmBody);

        var (completeStatus, completeBody) = await CompleteAsync(deviceClient, deviceAccess, pairingId, secret);
        Assert.Equal(HttpStatusCode.OK, completeStatus);
        using var completeDoc = JsonDocument.Parse(completeBody);
        var deviceId = completeDoc.RootElement.GetProperty("device_id").GetString()!;
        Assert.NotEmpty(deviceId);

        var (_, controlAccess, _) = await ControlTokenAsync();
        var list = await ListDevicesAsync(deviceClient, controlAccess);
        // The control token belongs to a different user: it must NOT see this device.
        Assert.Empty(list.GetProperty("devices").EnumerateArray());
    }

    [Fact]
    public async Task OwnerSeesOwnDevice_AfterHappyPath()
    {
        // Same flow, but the claiming browser user and the control
        // token user are the same account: the device shows up.
        var (deviceClient, deviceAccess, _) = await DeviceTokenAsync();
        var key = DeviceKeyPair.Generate();
        var init = await InitPairingAsync(deviceClient, deviceAccess, key, NewFingerprint());
        var pairingId = init.GetProperty("pairing_id").GetString()!;
        var userCode = init.GetProperty("user_code").GetString()!;
        var secret = init.GetProperty("initiator_secret").GetString()!;

        var email = NewEmail();
        var owner = await TestHelpers.CreateUserAsync(_factory, email, Password);
        var browser = TestHelpers.CreateClient(_factory);
        await TestHelpers.LoginAsync(browser, email, Password);
        var (_, confirmBody) = await ConfirmAsync(browser, userCode);
        Assert.Contains("claimed", confirmBody);
        var (status, _) = await CompleteAsync(deviceClient, deviceAccess, pairingId, secret);
        Assert.Equal(HttpStatusCode.OK, status);

        // Authorize the owner as muse-control to list their device.
        var (verifier, challenge) = TestHelpers.Pkce();
        var authUrl = "/connect/authorize?response_type=code"
            + "&client_id=muse-control&redirect_uri=http://127.0.0.1/callback"
            + "&scope=devices.read%20offline_access"
            + $"&code_challenge={challenge}&code_challenge_method=S256&state=s1";
        using var authorized = await TestHelpers.SubmitConsentAsync(browser, authUrl);
        var code = Q(authorized.Headers.Location!.ToString(), "code");
        using var token = await browser.PostAsync("/connect/token", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = "http://127.0.0.1/callback",
                ["client_id"] = "muse-control",
                ["code_verifier"] = verifier,
            }));
        using var doc = JsonDocument.Parse(await token.Content.ReadAsStringAsync());
        var ownerAccess2 = doc.RootElement.GetProperty("access_token").GetString()!;
        var list = await ListDevicesAsync(browser, ownerAccess2);
        var devices = list.GetProperty("devices").EnumerateArray().ToList();
        Assert.Single(devices);
        Assert.Equal("SAM-PC", devices[0].GetProperty("alias").GetString());
        Assert.Equal("Active", devices[0].GetProperty("status").GetString());
        Assert.Equal(key.Fingerprint, devices[0].GetProperty("key_fingerprint").GetString());
    }

    [Fact]
    public async Task ExpiredPairing_CannotBeClaimedOrCompleted()
    {
        var (deviceClient, deviceAccess, _) = await DeviceTokenAsync();
        var init = await InitPairingAsync(deviceClient, deviceAccess, DeviceKeyPair.Generate(), NewFingerprint());
        var pairingId = init.GetProperty("pairing_id").GetString()!;
        var userCode = init.GetProperty("user_code").GetString()!;
        var secret = init.GetProperty("initiator_secret").GetString()!;
        await ExpirePairingAsync(pairingId);

        var browser = await BrowserLoginAsync();
        using var page = await browser.GetAsync("/devices/pair?code=" + userCode);
        var pageBody = await page.Content.ReadAsStringAsync();
        Assert.Contains("expired", pageBody);

        var (status, body) = await CompleteAsync(deviceClient, deviceAccess, pairingId, secret);
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("not_claimed", body);
        var (_, controlAccess, _) = await ControlTokenAsync();
        _ = controlAccess;
    }

    [Fact]
    public async Task ExpiredAfterClaim_CompleteFailsAndNoDevice()
    {
        var (deviceClient, deviceAccess, _) = await DeviceTokenAsync();
        var init = await InitPairingAsync(deviceClient, deviceAccess, DeviceKeyPair.Generate(), NewFingerprint());
        var pairingId = init.GetProperty("pairing_id").GetString()!;
        var userCode = init.GetProperty("user_code").GetString()!;
        var secret = init.GetProperty("initiator_secret").GetString()!;
        var browser = await BrowserLoginAsync();
        var (_, confirmBody) = await ConfirmAsync(browser, userCode);
        Assert.Contains("claimed", confirmBody);
        await ExpirePairingAsync(pairingId);

        var (status, body) = await CompleteAsync(deviceClient, deviceAccess, pairingId, secret);
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("expired", body);
    }

    [Fact]
    public async Task WrongAccountClaim_IsVisible_AndSecondClaimFails()
    {
        var (deviceClient, deviceAccess, _) = await DeviceTokenAsync();
        var init = await InitPairingAsync(deviceClient, deviceAccess, DeviceKeyPair.Generate(), NewFingerprint());
        var pairingId = init.GetProperty("pairing_id").GetString()!;
        var userCode = init.GetProperty("user_code").GetString()!;
        var secret = init.GetProperty("initiator_secret").GetString()!;

        // Attacker (or the wrong account) claims the code first.
        var attackerEmail = NewEmail();
        var attacker = await TestHelpers.CreateUserAsync(_factory, attackerEmail, Password);
        var attackerBrowser = TestHelpers.CreateClient(_factory);
        await TestHelpers.LoginAsync(attackerBrowser, attackerEmail, Password);
        var (_, claimBody) = await ConfirmAsync(attackerBrowser, userCode);
        Assert.Contains("claimed", claimBody);

        // The rightful user now cannot claim the same code.
        var browser = await BrowserLoginAsync();
        using var page = await browser.GetAsync("/devices/pair?code=" + userCode);
        var secondBody = await page.Content.ReadAsStringAsync();
        Assert.True(secondBody.Contains("already") || secondBody.Contains("expired"),
            $"Second claim should fail: {secondBody}");

        // The device completes and LEARNS the owner is the attacker:
        // the wrong-account claim is visible on the device side.
        var (status, body) = await CompleteAsync(deviceClient, deviceAccess, pairingId, secret);
        Assert.Equal(HttpStatusCode.OK, status);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(attacker.Id, doc.RootElement.GetProperty("owner_id").GetString());
    }

    [Fact]
    public async Task ConcurrentClaims_OnlyOneWins()
    {
        var (deviceClient, deviceAccess, _) = await DeviceTokenAsync();
        var init = await InitPairingAsync(deviceClient, deviceAccess, DeviceKeyPair.Generate(), NewFingerprint());
        var userCode = init.GetProperty("user_code").GetString()!;

        var browserA = await BrowserLoginAsync();
        var browserB = await BrowserLoginAsync();
        var taskA = ConfirmAsync(browserA, userCode);
        var taskB = ConfirmAsync(browserB, userCode);
        var results = await Task.WhenAll(taskA, taskB);
        // Note: "already claimed" also contains "claimed" — match the
        // exact success phrase. The loser may report either "already
        // claimed" (lost the atomic UPDATE) or "expired or already
        // used" (loaded after the winner committed); both are correct.
        var claimed = results.Count(r => r.Body.Contains("Pairing claimed."));
        var rejected = results.Count(r =>
            r.Body.Contains("already claimed") || r.Body.Contains("expired or already used"));
        Assert.Equal(1, claimed);
        Assert.Equal(1, rejected);
    }

    [Fact]
    public async Task DuplicateRedemption_SecondCompleteFails_OneDeviceOnly()
    {
        var (deviceClient, deviceAccess, _) = await DeviceTokenAsync();
        var init = await InitPairingAsync(deviceClient, deviceAccess, DeviceKeyPair.Generate(), NewFingerprint());
        var pairingId = init.GetProperty("pairing_id").GetString()!;
        var userCode = init.GetProperty("user_code").GetString()!;
        var secret = init.GetProperty("initiator_secret").GetString()!;
        var browser = await BrowserLoginAsync();
        await ConfirmAsync(browser, userCode);
        var (first, _) = await CompleteAsync(deviceClient, deviceAccess, pairingId, secret);
        Assert.Equal(HttpStatusCode.OK, first);
        var (second, secondBody) = await CompleteAsync(deviceClient, deviceAccess, pairingId, secret);
        Assert.Equal(HttpStatusCode.BadRequest, second);
        Assert.Contains("already_redeemed", secondBody);
    }

    [Fact]
    public async Task CancelledPairing_CannotComplete()
    {
        var (deviceClient, deviceAccess, _) = await DeviceTokenAsync();
        var init = await InitPairingAsync(deviceClient, deviceAccess, DeviceKeyPair.Generate(), NewFingerprint());
        var pairingId = init.GetProperty("pairing_id").GetString()!;
        var userCode = init.GetProperty("user_code").GetString()!;
        var secret = init.GetProperty("initiator_secret").GetString()!;
        var browser = await BrowserLoginAsync();
        var (_, cancelBody) = await ConfirmAsync(browser, userCode, decision: "cancel");
        Assert.Contains("cancelled", cancelBody);
        var (status, body) = await CompleteAsync(deviceClient, deviceAccess, pairingId, secret);
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("cancelled", body);
    }

    [Fact]
    public async Task DeviceCancel_BeforeClaim_BlocksClaimAndComplete()
    {
        var (deviceClient, deviceAccess, _) = await DeviceTokenAsync();
        var init = await InitPairingAsync(deviceClient, deviceAccess, DeviceKeyPair.Generate(), NewFingerprint());
        var pairingId = init.GetProperty("pairing_id").GetString()!;
        var userCode = init.GetProperty("user_code").GetString()!;
        using var request = WithToken(HttpMethod.Post, "/api/v1/devices/pair/cancel", deviceAccess);
        request.Content = JsonContent.Create(new { pairing_id = pairingId });
        using var cancel = await deviceClient.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        var browser = await BrowserLoginAsync();
        using var page = await browser.GetAsync("/devices/pair?code=" + userCode);
        var confirmBody = await page.Content.ReadAsStringAsync();
        Assert.True(confirmBody.Contains("expired") || confirmBody.Contains("already"),
            $"Claim after cancel should fail: {confirmBody}");
    }

    [Fact]
    public async Task LeakedCodeWithoutSecret_CannotComplete()
    {
        var (deviceClient, deviceAccess, _) = await DeviceTokenAsync();
        var init = await InitPairingAsync(deviceClient, deviceAccess, DeviceKeyPair.Generate(), NewFingerprint());
        var pairingId = init.GetProperty("pairing_id").GetString()!;
        var userCode = init.GetProperty("user_code").GetString()!;
        var secret = init.GetProperty("initiator_secret").GetString()!;
        // Someone who only knows the code claims it — but cannot
        // complete without the high-entropy initiator secret.
        var browser = await BrowserLoginAsync();
        await ConfirmAsync(browser, userCode);
        var wrongSecret = PairingControllerBase64Url(new byte[32]);
        var (status, body) = await CompleteAsync(deviceClient, deviceAccess, pairingId, wrongSecret);
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("bad_initiator_secret", body);
        // The real device can still complete afterwards.
        var (okStatus, _) = await CompleteAsync(deviceClient, deviceAccess, pairingId, secret);
        Assert.Equal(HttpStatusCode.OK, okStatus);
    }

    [Fact]
    public async Task CompleteBeforeClaim_Fails()
    {
        var (deviceClient, deviceAccess, _) = await DeviceTokenAsync();
        var init = await InitPairingAsync(deviceClient, deviceAccess, DeviceKeyPair.Generate(), NewFingerprint());
        var (status, body) = await CompleteAsync(deviceClient, deviceAccess,
            init.GetProperty("pairing_id").GetString()!,
            init.GetProperty("initiator_secret").GetString()!);
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("not_claimed", body);
    }

    [Fact]
    public async Task InvalidCapabilitiesAndKeys_RejectedAtInit()
    {
        var (deviceClient, deviceAccess, _) = await DeviceTokenAsync();
        var key = DeviceKeyPair.Generate();
        var fp = NewFingerprint();
        // files.workspace.write needs separate enablement.
        using var badCaps = WithToken(HttpMethod.Post, "/api/v1/devices/pair/init", deviceAccess);
        badCaps.Content = JsonContent.Create(new
        {
            device_alias = "X",
            capabilities = new[] { "device.connect", "files.workspace.write" },
            machine_fingerprint = fp,
            public_key_der_base64 = Convert.ToBase64String(key.PublicKeyDer),
        });
        using var badCapsResp = await deviceClient.SendAsync(badCaps);
        Assert.Equal(HttpStatusCode.BadRequest, badCapsResp.StatusCode);
        Assert.Contains("invalid_capabilities", await badCapsResp.Content.ReadAsStringAsync());

        using var badKey = WithToken(HttpMethod.Post, "/api/v1/devices/pair/init", deviceAccess);
        badKey.Content = JsonContent.Create(new
        {
            device_alias = "X",
            capabilities = new[] { "device.connect" },
            machine_fingerprint = fp,
            public_key_der_base64 = Convert.ToBase64String(new byte[] { 1, 2, 3 }),
        });
        using var badKeyResp = await deviceClient.SendAsync(badKey);
        Assert.Equal(HttpStatusCode.BadRequest, badKeyResp.StatusCode);
        Assert.Contains("invalid_public_key", await badKeyResp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Revoke_AndOtherUserCannotRevoke()
    {
        var (deviceClient, deviceAccess, _) = await DeviceTokenAsync();
        var init = await InitPairingAsync(deviceClient, deviceAccess, DeviceKeyPair.Generate(), NewFingerprint());
        var pairingId = init.GetProperty("pairing_id").GetString()!;
        var userCode = init.GetProperty("user_code").GetString()!;
        var secret = init.GetProperty("initiator_secret").GetString()!;

        var email = NewEmail();
        var owner = await TestHelpers.CreateUserAsync(_factory, email, Password);
        var browser = TestHelpers.CreateClient(_factory);
        await TestHelpers.LoginAsync(browser, email, Password);
        await ConfirmAsync(browser, userCode);
        var (status, body) = await CompleteAsync(deviceClient, deviceAccess, pairingId, secret);
        Assert.Equal(HttpStatusCode.OK, status);
        var deviceId = JsonDocument.Parse(body).RootElement.GetProperty("device_id").GetString()!;

        // Owner's devices.read token revokes it.
        var (verifier, challenge) = TestHelpers.Pkce();
        var authUrl = "/connect/authorize?response_type=code"
            + "&client_id=muse-control&redirect_uri=http://127.0.0.1/callback"
            + "&scope=devices.read%20offline_access"
            + $"&code_challenge={challenge}&code_challenge_method=S256&state=s1";
        using var authorized = await TestHelpers.SubmitConsentAsync(browser, authUrl);
        var code = Q(authorized.Headers.Location!.ToString(), "code");
        using var token = await browser.PostAsync("/connect/token", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = "http://127.0.0.1/callback",
                ["client_id"] = "muse-control",
                ["code_verifier"] = verifier,
            }));
        using var doc = JsonDocument.Parse(await token.Content.ReadAsStringAsync());
        var ownerAccess = doc.RootElement.GetProperty("access_token").GetString()!;

        using var revoke = await browser.SendAsync(
            WithToken(HttpMethod.Delete, "/api/v1/devices/" + deviceId, ownerAccess));
        Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);
        var list = await ListDevicesAsync(browser, ownerAccess);
        var devices = list.GetProperty("devices").EnumerateArray().ToList();
        Assert.Single(devices);
        Assert.Equal("Revoked", devices[0].GetProperty("status").GetString());

        // A different user cannot revoke (and learns nothing).
        var (_, otherAccess, _) = await ControlTokenAsync();
        using var otherRevoke = await browser.SendAsync(
            WithToken(HttpMethod.Delete, "/api/v1/devices/" + deviceId, otherAccess));
        Assert.Equal(HttpStatusCode.NotFound, otherRevoke.StatusCode);

        // The device token itself (device.connect only) cannot revoke.
        using var deviceRevoke = await deviceClient.SendAsync(
            WithToken(HttpMethod.Delete, "/api/v1/devices/" + deviceId, deviceAccess));
        Assert.Equal(HttpStatusCode.Forbidden, deviceRevoke.StatusCode);
        _ = owner;
    }

    [Fact]
    public async Task CloneProtection_SameKeyDifferentMachine_Rejected()
    {
        var (deviceClient, deviceAccess, _) = await DeviceTokenAsync();
        var key = DeviceKeyPair.Generate();
        var fp1 = NewFingerprint();
        var init1 = await InitPairingAsync(deviceClient, deviceAccess, key, fp1);
        var browser = await BrowserLoginAsync();
        await ConfirmAsync(browser, init1.GetProperty("user_code").GetString()!);
        var (ok, _) = await CompleteAsync(deviceClient, deviceAccess,
            init1.GetProperty("pairing_id").GetString()!,
            init1.GetProperty("initiator_secret").GetString()!);
        Assert.Equal(HttpStatusCode.OK, ok);

        // Same key copied to a different machine: rejected.
        var fp2 = NewFingerprint();
        var init2 = await InitPairingAsync(deviceClient, deviceAccess, key, fp2);
        var browser2 = await BrowserLoginAsync();
        await ConfirmAsync(browser2, init2.GetProperty("user_code").GetString()!);
        var (clashStatus, clashBody) = await CompleteAsync(deviceClient, deviceAccess,
            init2.GetProperty("pairing_id").GetString()!,
            init2.GetProperty("initiator_secret").GetString()!);
        Assert.Equal(HttpStatusCode.BadRequest, clashStatus);
        Assert.Contains("key_in_use_on_another_machine", clashBody);
    }

    [Fact]
    public async Task Reinstall_NewKey_NewDeviceId()
    {
        var (deviceClient, deviceAccess, _) = await DeviceTokenAsync();
        var browser = await BrowserLoginAsync();
        var init1 = await InitPairingAsync(deviceClient, deviceAccess, DeviceKeyPair.Generate(), NewFingerprint());
        await ConfirmAsync(browser, init1.GetProperty("user_code").GetString()!);
        var (ok1, body1) = await CompleteAsync(deviceClient, deviceAccess,
            init1.GetProperty("pairing_id").GetString()!,
            init1.GetProperty("initiator_secret").GetString()!);
        Assert.Equal(HttpStatusCode.OK, ok1);
        var id1 = JsonDocument.Parse(body1).RootElement.GetProperty("device_id").GetString()!;

        // Reinstall = fresh keypair + fresh fingerprint: a new device.
        var init2 = await InitPairingAsync(deviceClient, deviceAccess, DeviceKeyPair.Generate(), NewFingerprint());
        await ConfirmAsync(browser, init2.GetProperty("user_code").GetString()!);
        var (ok2, body2) = await CompleteAsync(deviceClient, deviceAccess,
            init2.GetProperty("pairing_id").GetString()!,
            init2.GetProperty("initiator_secret").GetString()!);
        Assert.Equal(HttpStatusCode.OK, ok2);
        var id2 = JsonDocument.Parse(body2).RootElement.GetProperty("device_id").GetString()!;
        Assert.NotEqual(id1, id2);
    }

    [Fact]
    public async Task InitRateLimit_SixthInitForSameMachine_Is429()
    {
        using var factory = new RelayFactory();
        var (deviceClient, deviceAccess, _) = await DeviceTokenOn(factory);
        var fp = NewFingerprint();
        HttpStatusCode last = HttpStatusCode.OK;
        for (var i = 0; i < 6; i++)
        {
            using var request = WithToken(HttpMethod.Post, "/api/v1/devices/pair/init", deviceAccess);
            request.Content = JsonContent.Create(new
            {
                device_alias = "RATE",
                capabilities = new[] { "device.connect" },
                machine_fingerprint = fp,
                public_key_der_base64 = Convert.ToBase64String(DeviceKeyPair.Generate().PublicKeyDer),
            });
            using var response = await deviceClient.SendAsync(request);
            last = response.StatusCode;
            _ = await response.Content.ReadAsStringAsync();
        }
        Assert.Equal((HttpStatusCode)429, last);
    }

    private async Task<(HttpClient Client, string Access, string UserId)> DeviceTokenOn(RelayFactory factory)
    {
        var email = NewEmail();
        var user = await TestHelpers.CreateUserAsync(factory, email, Password);
        var client = TestHelpers.CreateClient(factory);
        await TestHelpers.LoginAsync(client, email, Password);
        var (verifier, challenge) = TestHelpers.Pkce();
        var authUrl = "/connect/authorize?response_type=code"
            + "&client_id=windows-agent&redirect_uri=http://127.0.0.1/callback"
            + "&scope=device.connect%20offline_access"
            + $"&code_challenge={challenge}&code_challenge_method=S256&state=s1";
        using var authorized = await TestHelpers.SubmitConsentAsync(client, authUrl);
        var code = Q(authorized.Headers.Location!.ToString(), "code");
        using var token = await client.PostAsync("/connect/token", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = "http://127.0.0.1/callback",
                ["client_id"] = "windows-agent",
                ["code_verifier"] = verifier,
            }));
        using var doc = JsonDocument.Parse(await token.Content.ReadAsStringAsync());
        return (client, doc.RootElement.GetProperty("access_token").GetString()!, user.Id);
    }

    private static string PairingControllerBase64Url(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
