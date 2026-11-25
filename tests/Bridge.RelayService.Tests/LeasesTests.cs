using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Bridge.DeviceChannel;
using Bridge.DeviceIdentity;
using System.Web;
using Bridge.RelayService.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Bridge.RelayService.Tests;

/// <summary>
/// T12: consent sessions + control leases (Kestrel, real HTTP + WSS).
/// </summary>
public sealed class LeasesTests : IClassFixture<KestrelFixture>
{
    private const string Password = "T3st!Passw0rd";

    private readonly KestrelFixture _fixture;
    public LeasesTests(KestrelFixture fixture) => _fixture = fixture;

    private static string NewEmail() => $"lease-{Guid.NewGuid():N}@example.test";

    private HttpClient NewHttpClient()
    {
        var handler = new HttpClientHandler
        {
            CookieContainer = new CookieContainer(),
            UseCookies = true,
            AllowAutoRedirect = false,
        };
        return new HttpClient(handler) { BaseAddress = new Uri(_fixture.BaseUrl) };
    }

    private async Task<string> CreateUserAsync(string email)
    {
        using var scope = _fixture.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = new ApplicationUser { UserName = email, Email = email };
        var result = await users.CreateAsync(user, Password);
        Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(e => e.Description)));
        return user.Id;
    }

    private static string Q(string url, string name)
    {
        var uri = new Uri(url);
        return HttpUtility.ParseQueryString(uri.Query)[name]
            ?? throw new InvalidOperationException($"Missing {name} in {url}.");
    }

    /// <summary>Full auth-code flow for one client, returns the access token.</summary>
    private async Task<string> AuthorizeAsync(HttpClient client, string clientId, string scope)
    {
        var (verifier, challenge) = TestHelpers.Pkce();
        var authUrl = "/connect/authorize?response_type=code"
            + $"&client_id={clientId}&redirect_uri={Uri.EscapeDataString("http://127.0.0.1/callback")}"
            + $"&scope={Uri.EscapeDataString(scope + " offline_access")}"
            + $"&code_challenge={challenge}&code_challenge_method=S256&state=s1";
        using var authorized = await TestHelpers.SubmitConsentAsync(client, authUrl);
        var body0 = await authorized.Content.ReadAsStringAsync();
        Assert.True(authorized.StatusCode == HttpStatusCode.Found,
            $"Authorize failed: {(int)authorized.StatusCode} {body0}");
        var code = Q(authorized.Headers.Location!.ToString(), "code");
        using var token = await client.PostAsync("/connect/token", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = "http://127.0.0.1/callback",
                ["client_id"] = clientId,
                ["code_verifier"] = verifier,
            }));
        var body = await token.Content.ReadAsStringAsync();
        Assert.True(token.IsSuccessStatusCode, $"Token failed: {(int)token.StatusCode} {body}");
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("access_token").GetString()!;
    }

    private sealed record BoundDevice(
        string DeviceId, string DeviceAccess, string ControlAccess, DeviceKeyPair Key);

    private async Task<BoundDevice> BindDeviceAsync()
    {
        var email = NewEmail();
        await CreateUserAsync(email);
        var browser = NewHttpClient();
        await TestHelpers.LoginAsync(browser, email, Password);
        var deviceToken = await AuthorizeAsync(browser, "windows-agent", "device.connect");
        var controlToken = await AuthorizeAsync(browser, "muse-control",
            "devices.read desktop.observe desktop.input");
        var keyPair = DeviceKeyPair.Generate();
        var fingerprint = MachineFingerprint.Compute(Guid.NewGuid().ToString("D"), "Windows 11");
        var deviceHttp = NewHttpClient();
        deviceHttp.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", deviceToken);
        using var init = await deviceHttp.PostAsJsonAsync("/api/v1/devices/pair/init", new
        {
            device_alias = "SAM-PC",
            capabilities = new[] { "device.connect", "desktop.observe", "desktop.input" },
            machine_fingerprint = fingerprint,
            public_key_der_base64 = Convert.ToBase64String(keyPair.PublicKeyDer),
        });
        var initBody = await init.Content.ReadAsStringAsync();
        Assert.True(init.IsSuccessStatusCode, $"Pairing init failed: {(int)init.StatusCode} {initBody}");
        using var initDoc = JsonDocument.Parse(initBody);
        var pairingId = initDoc.RootElement.GetProperty("pairing_id").GetString()!;
        var userCode = initDoc.RootElement.GetProperty("user_code").GetString()!;
        var initiatorSecret = initDoc.RootElement.GetProperty("initiator_secret").GetString()!;

        using var page = await browser.GetAsync("/devices/pair?code=" + userCode);
        var token = TestHelpers.AntiforgeryToken(await page.Content.ReadAsStringAsync());
        using var confirm = await browser.PostAsync("/devices/pair/confirm",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["user_code"] = userCode,
                ["decision"] = "confirm",
            }));
        Assert.Contains("claimed", await confirm.Content.ReadAsStringAsync());

        using var complete = await deviceHttp.PostAsJsonAsync("/api/v1/devices/pair/complete", new
        {
            pairing_id = pairingId,
            initiator_secret = initiatorSecret,
        });
        var completeBody = await complete.Content.ReadAsStringAsync();
        Assert.True(complete.IsSuccessStatusCode, $"Pairing complete failed: {completeBody}");
        using var completeDoc = JsonDocument.Parse(completeBody);
        return new BoundDevice(
            completeDoc.RootElement.GetProperty("device_id").GetString()!,
            deviceToken, controlToken, keyPair);
    }

    private Uri WsUri(string deviceId) =>
        new(_fixture.BaseUrl.Replace("http://", "ws://") + "/ws/device?device_id=" + deviceId);

    private async Task<(DeviceChannelClient Client, Task Run, int Epoch)> ConnectAsync(
        BoundDevice device, CancellationTokenSource cts)
    {
        var client = new DeviceChannelClient();
        var connected = new TaskCompletionSource<int>();
        client.OnConnected += e => connected.TrySetResult(e);
        var run = client.RunAsync(WsUri(device.DeviceId), device.DeviceAccess,
            device.DeviceId, device.Key, (body, _) => Task.FromResult(body), cts.Token);
        var epoch = await connected.Task.WaitAsync(TimeSpan.FromSeconds(15));
        return (client, run, epoch);
    }

    private async Task<string> CreateConsentAsync(BoundDevice device, int durationSeconds = 7200,
        string[]? scopes = null)
    {
        var http = NewHttpClient();
        http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", device.ControlAccess);
        using var create = await http.PostAsJsonAsync("/api/v1/leases", new
        {
            device_id = device.DeviceId,
            scopes = scopes ?? new[] { "desktop.observe", "desktop.input" },
            duration_seconds = durationSeconds,
        });
        var body = await create.Content.ReadAsStringAsync();
        Assert.True((int)create.StatusCode == 202, $"Consent create failed: {(int)create.StatusCode} {body}");
        return JsonDocument.Parse(body).RootElement.GetProperty("consent_id").GetString()!;
    }

    private async Task ApproveAsync(BoundDevice device, string consentId, bool approved = true)
    {
        var http = NewHttpClient();
        http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", device.DeviceAccess);
        using var decide = await http.PostAsJsonAsync(
            $"/api/v1/devices/{device.DeviceId}/consent/{consentId}/decide",
            new { approved });
        Assert.True(decide.IsSuccessStatusCode, $"Decide failed: {(int)decide.StatusCode}");
    }

    private async Task<(string LeaseId, int Epoch)> AcquireAsync(
        BoundDevice device, string consentId, string mode = "write", int ttlSeconds = 60)
    {
        var http = NewHttpClient();
        http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", device.ControlAccess);
        using var acquire = await http.PostAsJsonAsync($"/api/v1/leases/{consentId}/acquire",
            new { mode, ttl_seconds = ttlSeconds });
        var body = await acquire.Content.ReadAsStringAsync();
        Assert.True(acquire.IsSuccessStatusCode, $"Acquire failed: {(int)acquire.StatusCode} {body}");
        using var doc = JsonDocument.Parse(body);
        return (doc.RootElement.GetProperty("lease_id").GetString()!,
            doc.RootElement.GetProperty("epoch").GetInt32());
    }

    private static object Envelope(
        string deviceId, string leaseId, int epoch, int sequence,
        string operation, object payload, Guid? commandId = null,
        DateTimeOffset? issuedAt = null, int ttlSeconds = 15)
    {
        var issued = issuedAt ?? DateTimeOffset.UtcNow;
        return new
        {
            schema_version = "1.0",
            command_id = (commandId ?? Guid.NewGuid()).ToString(),
            device_id = deviceId,
            lease_id = leaseId,
            epoch,
            sequence,
            operation,
            issued_at = issued,
            expires_at = issued.AddSeconds(ttlSeconds),
            payload,
        };
    }

    private async Task<HttpResponseMessage> SendCommandAsync(
        BoundDevice device, object envelope)
    {
        var http = NewHttpClient();
        http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", device.ControlAccess);
        return await http.PostAsJsonAsync(
            $"/api/v1/devices/{device.DeviceId}/commands", envelope);
    }

    [Fact]
    public async Task ConsentPending_AcquireReturnsPending()
    {
        var device = await BindDeviceAsync();
        var consentId = await CreateConsentAsync(device);
        var http = NewHttpClient();
        http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", device.ControlAccess);
        using var acquire = await http.PostAsJsonAsync($"/api/v1/leases/{consentId}/acquire",
            new { mode = "write", ttl_seconds = 60 });
        Assert.Equal(HttpStatusCode.Conflict, acquire.StatusCode);
        Assert.Contains("consent_pending", await acquire.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ConsentDenied_AcquireReturnsDenied()
    {
        var device = await BindDeviceAsync();
        var consentId = await CreateConsentAsync(device);
        await ApproveAsync(device, consentId, approved: false);
        var http = NewHttpClient();
        http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", device.ControlAccess);
        using var acquire = await http.PostAsJsonAsync($"/api/v1/leases/{consentId}/acquire",
            new { mode = "write", ttl_seconds = 60 });
        Assert.Equal(HttpStatusCode.Conflict, acquire.StatusCode);
        Assert.Contains("consent_denied", await acquire.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task SecondWriteLease_IsHeld()
    {
        var device = await BindDeviceAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var (client, run, _) = await ConnectAsync(device, cts);
        var consentId = await CreateConsentAsync(device);
        await ApproveAsync(device, consentId);
        var (leaseId, _) = await AcquireAsync(device, consentId, "write");
        var http = NewHttpClient();
        http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", device.ControlAccess);
        using var second = await http.PostAsJsonAsync($"/api/v1/leases/{consentId}/acquire",
            new { mode = "write", ttl_seconds = 60 });
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        var body = await second.Content.ReadAsStringAsync();
        Assert.Contains("lease_held", body);
        Assert.Contains("Held by", body);

        // Releasing frees the device for a new write lease.
        using var release = await http.DeleteAsync($"/api/v1/leases/{leaseId}");
        Assert.True(release.IsSuccessStatusCode);
        var (leaseId2, _) = await AcquireAsync(device, consentId, "write");
        Assert.NotEqual(leaseId, leaseId2);
        cts.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task ObserveLeases_AreShared()
    {
        var device = await BindDeviceAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var (client, run, _) = await ConnectAsync(device, cts);
        var consentId = await CreateConsentAsync(device);
        await ApproveAsync(device, consentId);
        var (l1, _) = await AcquireAsync(device, consentId, "observe");
        var (l2, _) = await AcquireAsync(device, consentId, "observe");
        Assert.NotEqual(l1, l2);
        cts.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task SequenceEnforcement_RejectsOutOfOrder()
    {
        var device = await BindDeviceAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var (client, run, epoch) = await ConnectAsync(device, cts);
        var consentId = await CreateConsentAsync(device);
        await ApproveAsync(device, consentId);
        var (leaseId, _) = await AcquireAsync(device, consentId, "write");

        using var first = await SendCommandAsync(device,
            Envelope(device.DeviceId, leaseId, epoch, 1, "test.echo", new { n = 1 }));
        Assert.True(first.IsSuccessStatusCode, await first.Content.ReadAsStringAsync());

        using var replaySeq = await SendCommandAsync(device,
            Envelope(device.DeviceId, leaseId, epoch, 1, "test.echo", new { n = 2 }));
        Assert.Equal(HttpStatusCode.Conflict, replaySeq.StatusCode);
        Assert.Contains("stale_sequence", await replaySeq.Content.ReadAsStringAsync());

        using var gap = await SendCommandAsync(device,
            Envelope(device.DeviceId, leaseId, epoch, 4, "test.echo", new { n = 3 }));
        Assert.Equal(HttpStatusCode.Conflict, gap.StatusCode);
        Assert.Contains("stale_sequence", await gap.Content.ReadAsStringAsync());

        using var second = await SendCommandAsync(device,
            Envelope(device.DeviceId, leaseId, epoch, 2, "test.echo", new { n = 4 }));
        Assert.True(second.IsSuccessStatusCode, await second.Content.ReadAsStringAsync());
        cts.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task ReplayIdempotency_ReturnsStoredReceipt_ConflictOnDifferentBinding()
    {
        var device = await BindDeviceAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var (client, run, epoch) = await ConnectAsync(device, cts);
        var consentId = await CreateConsentAsync(device);
        await ApproveAsync(device, consentId);
        var (leaseId, _) = await AcquireAsync(device, consentId, "write");

        var commandId = Guid.NewGuid();
        var envelope = Envelope(device.DeviceId, leaseId, epoch, 1, "test.echo",
            new { ping = "once" }, commandId);
        using var first = await SendCommandAsync(device, envelope);
        var firstBody = await first.Content.ReadAsStringAsync();
        Assert.True(first.IsSuccessStatusCode, firstBody);

        using var replay = await SendCommandAsync(device, envelope);
        var replayBody = await replay.Content.ReadAsStringAsync();
        Assert.True(replay.IsSuccessStatusCode, replayBody);
        using var replayDoc = JsonDocument.Parse(replayBody);
        Assert.True(replayDoc.RootElement.GetProperty("replayed").GetBoolean());
        Assert.Equal("succeeded", replayDoc.RootElement.GetProperty("status").GetString());

        var changed = Envelope(device.DeviceId, leaseId, epoch, 1, "test.echo",
            new { ping = "different" }, commandId);
        using var conflict = await SendCommandAsync(device, changed);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Contains("idempotency_conflict", await conflict.Content.ReadAsStringAsync());
        cts.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Reconnect_StalesOldLease()
    {
        var device = await BindDeviceAsync();
        using var cts1 = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var (client1, run1, epoch1) = await ConnectAsync(device, cts1);
        var consentId = await CreateConsentAsync(device);
        await ApproveAsync(device, consentId);
        var (leaseId, _) = await AcquireAsync(device, consentId, "write");
        cts1.Cancel();
        await run1.WaitAsync(TimeSpan.FromSeconds(10));

        using var cts2 = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var (client2, run2, epoch2) = await ConnectAsync(device, cts2);
        Assert.Equal(epoch1 + 1, epoch2);

        using var response = await SendCommandAsync(device,
            Envelope(device.DeviceId, leaseId, epoch1, 1, "test.echo", new { ping = "stale" }));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("stale_lease", await response.Content.ReadAsStringAsync());
        cts2.Cancel();
        await run2.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Pause_BlocksCommands_ResumeUnblocks_LocalOnly()
    {
        var device = await BindDeviceAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var (client, run, epoch) = await ConnectAsync(device, cts);
        var consentId = await CreateConsentAsync(device);
        await ApproveAsync(device, consentId);
        var (leaseId, _) = await AcquireAsync(device, consentId, "write");

        client.LocalPaused = true;
        await client.ReportLocalStateAsync(cts.Token);
        await Task.Delay(500); // let the server process the frame
        using var blocked = await SendCommandAsync(device,
            Envelope(device.DeviceId, leaseId, epoch, 1, "test.echo", new { ping = "p" }));
        Assert.Equal((HttpStatusCode)423, blocked.StatusCode);
        var blockedBody = await blocked.Content.ReadAsStringAsync();
        Assert.Contains("local_paused", blockedBody);
        Assert.Contains("local_resume", blockedBody);

        // The pause does not release the lease.
        var http = NewHttpClient();
        http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", device.ControlAccess);
        using var lease = await http.GetAsync($"/api/v1/leases/{leaseId}");
        using var leaseDoc = JsonDocument.Parse(await lease.Content.ReadAsStringAsync());
        Assert.Equal("Active", leaseDoc.RootElement.GetProperty("status").GetString());

        client.LocalPaused = false;
        await client.ReportLocalStateAsync(cts.Token);
        await Task.Delay(500);
        using var ok = await SendCommandAsync(device,
            Envelope(device.DeviceId, leaseId, epoch, 1, "test.echo", new { ping = "resumed" }));
        Assert.True(ok.IsSuccessStatusCode, await ok.Content.ReadAsStringAsync());
        cts.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Lock_BlocksCommands()
    {
        var device = await BindDeviceAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var (client, run, epoch) = await ConnectAsync(device, cts);
        var consentId = await CreateConsentAsync(device);
        await ApproveAsync(device, consentId);
        var (leaseId, _) = await AcquireAsync(device, consentId, "write");

        client.LocalLocked = true;
        await client.ReportLocalStateAsync(cts.Token);
        await Task.Delay(500);
        using var blocked = await SendCommandAsync(device,
            Envelope(device.DeviceId, leaseId, epoch, 1, "test.echo", new { ping = "l" }));
        Assert.Equal((HttpStatusCode)423, blocked.StatusCode);
        Assert.Contains("local_locked", await blocked.Content.ReadAsStringAsync());
        cts.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task AgentSideRefusal_WhenServerDoesNotKnowYet()
    {
        var device = await BindDeviceAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var (client, run, epoch) = await ConnectAsync(device, cts);
        var consentId = await CreateConsentAsync(device);
        await ApproveAsync(device, consentId);
        var (leaseId, _) = await AcquireAsync(device, consentId, "write");

        // Pause locally WITHOUT reporting: the server still dispatches,
        // the agent itself must refuse to execute.
        client.LocalPaused = true;
        using var response = await SendCommandAsync(device,
            Envelope(device.DeviceId, leaseId, epoch, 1, "test.echo", new { ping = "agent" }));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("failed", doc.RootElement.GetProperty("status").GetString());
        Assert.Equal("local_paused",
            doc.RootElement.GetProperty("result").GetProperty("error").GetString());
        cts.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task LeaseExpiry_BlocksCommands_RenewExtends()
    {
        var device = await BindDeviceAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var (client, run, epoch) = await ConnectAsync(device, cts);
        var consentId = await CreateConsentAsync(device);
        await ApproveAsync(device, consentId);
        var (leaseId, _) = await AcquireAsync(device, consentId, "write", ttlSeconds: 5);

        // Renew after 3 s: the lease survives past its first deadline.
        await Task.Delay(TimeSpan.FromSeconds(3));
        var http = NewHttpClient();
        http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", device.ControlAccess);
        using var renew = await http.PostAsJsonAsync($"/api/v1/leases/{leaseId}/renew",
            new { ttl_seconds = 5 });
        Assert.True(renew.IsSuccessStatusCode, await renew.Content.ReadAsStringAsync());

        await Task.Delay(TimeSpan.FromSeconds(3)); // 6 s after acquire, past the first deadline
        using var ok = await SendCommandAsync(device,
            Envelope(device.DeviceId, leaseId, epoch, 1, "test.echo", new { ping = "renewed" }));
        Assert.True(ok.IsSuccessStatusCode, await ok.Content.ReadAsStringAsync());

        // Now let it lapse without renewing.
        await Task.Delay(TimeSpan.FromSeconds(6));
        using var expired = await SendCommandAsync(device,
            Envelope(device.DeviceId, leaseId, epoch, 2, "test.echo", new { ping = "late" }));
        Assert.Equal(HttpStatusCode.Conflict, expired.StatusCode);
        Assert.Contains("lease_expired", await expired.Content.ReadAsStringAsync());
        cts.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task ConsentExpiry_BlocksAcquire()
    {
        var device = await BindDeviceAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var (client, run, _) = await ConnectAsync(device, cts);
        var consentId = await CreateConsentAsync(device, durationSeconds: 60);
        await ApproveAsync(device, consentId);
        // Backdate the consent to expired, the way a real expiry looks.
        using (var scope = _fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var consent = await db.ConsentSessions.FindAsync(consentId);
            consent!.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1);
            await db.SaveChangesAsync();
        }
        var http = NewHttpClient();
        http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", device.ControlAccess);
        using var acquire = await http.PostAsJsonAsync($"/api/v1/leases/{consentId}/acquire",
            new { mode = "write", ttl_seconds = 60 });
        Assert.Equal(HttpStatusCode.Conflict, acquire.StatusCode);
        Assert.Contains("consent_expired", await acquire.Content.ReadAsStringAsync());
        cts.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task EnvelopeValidation_RejectsClockSkew_BadTtl_Expired_UnknownField()
    {
        var device = await BindDeviceAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var (client, run, epoch) = await ConnectAsync(device, cts);
        var consentId = await CreateConsentAsync(device);
        await ApproveAsync(device, consentId);
        var (leaseId, _) = await AcquireAsync(device, consentId, "write");

        using var skew = await SendCommandAsync(device, Envelope(device.DeviceId, leaseId, epoch, 1,
            "test.echo", new { ping = "skew" }, issuedAt: DateTimeOffset.UtcNow.AddSeconds(30)));
        Assert.Equal(HttpStatusCode.BadRequest, skew.StatusCode);
        Assert.Contains("clock_skew", await skew.Content.ReadAsStringAsync());

        using var badTtl = await SendCommandAsync(device, Envelope(device.DeviceId, leaseId, epoch, 1,
            "test.echo", new { ping = "ttl" }, ttlSeconds: 45));
        Assert.Equal(HttpStatusCode.BadRequest, badTtl.StatusCode);
        Assert.Contains("bad_ttl", await badTtl.Content.ReadAsStringAsync());

        using var expired = await SendCommandAsync(device, Envelope(device.DeviceId, leaseId, epoch, 1,
            "test.echo", new { ping = "exp" }, issuedAt: DateTimeOffset.UtcNow.AddSeconds(-20)));
        Assert.Equal(HttpStatusCode.Gone, expired.StatusCode);
        Assert.Contains("command_expired", await expired.Content.ReadAsStringAsync());

        // Unknown field: build the JSON by hand.
        var raw = $$"""
            {"schema_version":"1.0","command_id":"{{Guid.NewGuid()}}","device_id":"{{device.DeviceId}}","lease_id":"{{leaseId}}","epoch":{{epoch}},"sequence":1,"operation":"test.echo","issued_at":"{{DateTimeOffset.UtcNow:o}}","expires_at":"{{DateTimeOffset.UtcNow.AddSeconds(15):o}}","payload":{"ping":"uf"},"surprise":true}
            """;
        var http = NewHttpClient();
        http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", device.ControlAccess);
        using var unknown = await http.PostAsync(
            $"/api/v1/devices/{device.DeviceId}/commands",
            new StringContent(raw, System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Contains("unknown_field", await unknown.Content.ReadAsStringAsync());
        cts.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task CommandStatus_CanBeQueried()
    {
        var device = await BindDeviceAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var (client, run, epoch) = await ConnectAsync(device, cts);
        var consentId = await CreateConsentAsync(device);
        await ApproveAsync(device, consentId);
        var (leaseId, _) = await AcquireAsync(device, consentId, "write");

        var commandId = Guid.NewGuid();
        using var sent = await SendCommandAsync(device,
            Envelope(device.DeviceId, leaseId, epoch, 1, "test.echo", new { ping = "q" }, commandId));
        Assert.True(sent.IsSuccessStatusCode, await sent.Content.ReadAsStringAsync());

        var http = NewHttpClient();
        http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", device.ControlAccess);
        using var queried = await http.GetAsync($"/api/v1/commands/{commandId}");
        Assert.True(queried.IsSuccessStatusCode);
        using var doc = JsonDocument.Parse(await queried.Content.ReadAsStringAsync());
        Assert.Equal("succeeded", doc.RootElement.GetProperty("status").GetString());

        using var missing = await http.GetAsync($"/api/v1/commands/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        cts.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Release_BlocksFurtherCommands()
    {
        var device = await BindDeviceAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var (client, run, epoch) = await ConnectAsync(device, cts);
        var consentId = await CreateConsentAsync(device);
        await ApproveAsync(device, consentId);
        var (leaseId, _) = await AcquireAsync(device, consentId, "write");

        using var first = await SendCommandAsync(device,
            Envelope(device.DeviceId, leaseId, epoch, 1, "test.echo", new { ping = "1" }));
        Assert.True(first.IsSuccessStatusCode);

        var http = NewHttpClient();
        http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", device.ControlAccess);
        using var release = await http.DeleteAsync($"/api/v1/leases/{leaseId}");
        Assert.True(release.IsSuccessStatusCode);

        using var after = await SendCommandAsync(device,
            Envelope(device.DeviceId, leaseId, epoch, 2, "test.echo", new { ping = "2" }));
        Assert.Equal(HttpStatusCode.Conflict, after.StatusCode);
        Assert.Contains("lease_expired", await after.Content.ReadAsStringAsync());
        cts.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task TypeText_And_FileOps_RouteThroughCommandChannel()
    {
        // Regression: the relay once mapped "input.type" instead of the
        // canonical "input.type_text", so real typing would have been
        // rejected as unknown_operation. file.* operations are new in
        // T22 and ride the same channel.
        var device = await BindDeviceAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var (client, run, epoch) = await ConnectAsync(device, cts);
        var consentId = await CreateConsentAsync(device);
        await ApproveAsync(device, consentId);
        var (writeLease, _) = await AcquireAsync(device, consentId, "write");
        var (observeLease, _) = await AcquireAsync(device, consentId, "observe");

        using var typeText = await SendCommandAsync(device,
            Envelope(device.DeviceId, writeLease, epoch, 1, "input.type_text", new { text = "你好" }));
        Assert.True(typeText.IsSuccessStatusCode, await typeText.Content.ReadAsStringAsync());

        using var fileRead = await SendCommandAsync(device,
            Envelope(device.DeviceId, observeLease, epoch, 1, "file.read", new { path = "notes.txt" }));
        Assert.True(fileRead.IsSuccessStatusCode, await fileRead.Content.ReadAsStringAsync());

        using var fileWrite = await SendCommandAsync(device,
            Envelope(device.DeviceId, writeLease, epoch, 2, "file.write",
                new { path = "notes.txt", content_b64 = "aGVsbG8=", create_only = true }));
        Assert.True(fileWrite.IsSuccessStatusCode, await fileWrite.Content.ReadAsStringAsync());

        using var fileMove = await SendCommandAsync(device,
            Envelope(device.DeviceId, writeLease, epoch, 3, "file.move",
                new { source_path = "notes.txt", dest_path = "archive/notes.txt" }));
        Assert.True(fileMove.IsSuccessStatusCode, await fileMove.Content.ReadAsStringAsync());
        cts.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task ObserveLease_CannotInput_OrWriteFiles()
    {
        var device = await BindDeviceAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var (client, run, epoch) = await ConnectAsync(device, cts);
        var consentId = await CreateConsentAsync(device);
        await ApproveAsync(device, consentId);
        var (observeLease, _) = await AcquireAsync(device, consentId, "observe");

        using var click = await SendCommandAsync(device,
            Envelope(device.DeviceId, observeLease, epoch, 1, "input.click", new { x = 10, y = 10 }));
        Assert.Equal(HttpStatusCode.Forbidden, click.StatusCode);
        Assert.Contains("lease_mode", await click.Content.ReadAsStringAsync());

        using var fileWrite = await SendCommandAsync(device,
            Envelope(device.DeviceId, observeLease, epoch, 1, "file.write",
                new { path = "notes.txt", content_b64 = "eA==", create_only = true }));
        Assert.Equal(HttpStatusCode.Forbidden, fileWrite.StatusCode);
        Assert.Contains("lease_mode", await fileWrite.Content.ReadAsStringAsync());
        cts.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task WriteLease_RequiresInputScopeInConsent()
    {
        var device = await BindDeviceAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var (client, run, epoch) = await ConnectAsync(device, cts);
        var consentId = await CreateConsentAsync(device, scopes: new[] { "desktop.observe" });
        await ApproveAsync(device, consentId);

        var http = NewHttpClient();
        http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", device.ControlAccess);
        using var acquire = await http.PostAsJsonAsync($"/api/v1/leases/{consentId}/acquire",
            new { mode = "write", ttl_seconds = 60 });
        Assert.Equal(HttpStatusCode.Conflict, acquire.StatusCode);
        Assert.Contains("consent_scope", await acquire.Content.ReadAsStringAsync());
        cts.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
