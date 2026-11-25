using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Web;
using Bridge.DeviceChannel;
using Bridge.DeviceIdentity;
using Bridge.RelayService.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Bridge.RelayService.Tests;

/// <summary>
/// T11 device channel, against the real Kestrel server: handshake,
/// challenge signature, eviction, heartbeat, command round-trip,
/// offline/scope errors, revocation propagation, frame limits.
/// </summary>
public sealed class DeviceChannelTests : IClassFixture<KestrelFixture>
{
    private readonly KestrelFixture _fixture;
    private const string Password = "Correct-Horse-99!";

    private static string NewEmail() => "chan-" + Guid.NewGuid().ToString("N")[..8] + "@example.test";
    private static string NewFingerprint() =>
        MachineFingerprint.Compute(Guid.NewGuid().ToString("D"), "Windows 11");

    public DeviceChannelTests(KestrelFixture fixture) => _fixture = fixture;

    private HttpClient NewHttpClient()
    {
        var handler = new HttpClientHandler
        {
            CookieContainer = new CookieContainer(),
            UseCookies = true,
            // Do NOT follow redirects: LoginAsync asserts on the 302
            // itself, and the app has no home page to land on.
            AllowAutoRedirect = false,
        };
        return new HttpClient(handler) { BaseAddress = new Uri(_fixture.BaseUrl) };
    }

    private async Task<ApplicationUser> CreateUserAsync(string email)
    {
        using var scope = _fixture.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = new ApplicationUser { UserName = email, Email = email };
        var result = await users.CreateAsync(user, Password);
        Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(e => e.Description)));
        return user;
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
        string DeviceId, DeviceKeyPair Key, string Fingerprint,
        string DeviceAccess, string ControlAccess, string OwnerId);

    /// <summary>Bind a fresh device for a fresh user; returns both tokens.</summary>
    private async Task<BoundDevice> BindDeviceAsync()
    {
        var email = NewEmail();
        var owner = await CreateUserAsync(email);
        var browser = NewHttpClient();
        await TestHelpers.LoginAsync(browser, email, Password);
        var deviceAccess = await AuthorizeAsync(browser, "windows-agent", "device.connect");
        var controlAccess = await AuthorizeAsync(browser, "muse-control",
            "devices.read desktop.observe desktop.input");

        var key = DeviceKeyPair.Generate();
        var fp = NewFingerprint();
        var deviceHttp = NewHttpClient();
        deviceHttp.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", deviceAccess);
        using var init = await deviceHttp.PostAsJsonAsync("/api/v1/devices/pair/init", new
        {
            device_alias = "WS-PC",
            capabilities = new[] { "device.connect", "desktop.observe", "desktop.input" },
            machine_fingerprint = fp,
            public_key_der_base64 = Convert.ToBase64String(key.PublicKeyDer),
        });
        var initBody = await init.Content.ReadAsStringAsync();
        Assert.True(init.IsSuccessStatusCode, $"Init failed: {(int)init.StatusCode} {initBody}");
        using var initDoc = JsonDocument.Parse(initBody);
        var pairingId = initDoc.RootElement.GetProperty("pairing_id").GetString()!;
        var userCode = initDoc.RootElement.GetProperty("user_code").GetString()!;
        var secret = initDoc.RootElement.GetProperty("initiator_secret").GetString()!;

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
            initiator_secret = secret,
        });
        var completeBody = await complete.Content.ReadAsStringAsync();
        Assert.True(complete.IsSuccessStatusCode, $"Complete failed: {(int)complete.StatusCode} {completeBody}");
        var deviceId = JsonDocument.Parse(completeBody).RootElement.GetProperty("device_id").GetString()!;
        return new BoundDevice(deviceId, key, fp, deviceAccess, controlAccess, owner.Id);
    }

    private Uri WsUri(string deviceId) =>
        new(_fixture.BaseUrl.Replace("http://", "ws://") + "/ws/device?device_id=" + deviceId);

    /// <summary>Consent -> device approve -> acquire. Returns (lease_id, epoch).</summary>
    private async Task<(string LeaseId, int Epoch)> AcquireLeaseAsync(
        BoundDevice device, string mode = "write", int ttlSeconds = 60)
    {
        var http = NewHttpClient();
        http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", device.ControlAccess);
        using var create = await http.PostAsJsonAsync("/api/v1/leases", new
        {
            device_id = device.DeviceId,
            scopes = new[] { "desktop.observe", "desktop.input" },
            duration_seconds = 7200,
        });
        var createBody = await create.Content.ReadAsStringAsync();
        Assert.True((int)create.StatusCode == 202, $"Consent create failed: {(int)create.StatusCode} {createBody}");
        var consentId = JsonDocument.Parse(createBody).RootElement.GetProperty("consent_id").GetString()!;

        var deviceHttp = NewHttpClient();
        deviceHttp.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", device.DeviceAccess);
        using var decide = await deviceHttp.PostAsJsonAsync(
            $"/api/v1/devices/{device.DeviceId}/consent/{consentId}/decide",
            new { approved = true });
        Assert.True(decide.IsSuccessStatusCode, $"Decide failed: {(int)decide.StatusCode}");

        using var acquire = await http.PostAsJsonAsync($"/api/v1/leases/{consentId}/acquire",
            new { mode, ttl_seconds = ttlSeconds });
        var acquireBody = await acquire.Content.ReadAsStringAsync();
        Assert.True(acquire.IsSuccessStatusCode, $"Acquire failed: {(int)acquire.StatusCode} {acquireBody}");
        using var doc = JsonDocument.Parse(acquireBody);
        return (doc.RootElement.GetProperty("lease_id").GetString()!,
            doc.RootElement.GetProperty("epoch").GetInt32());
    }

    private static object Envelope(
        string deviceId, string leaseId, int epoch, int sequence,
        string operation, object payload, Guid? commandId = null)
    {
        var issued = DateTimeOffset.UtcNow;
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
            expires_at = issued.AddSeconds(15),
            payload,
        };
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, string what)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
                return;
            await Task.Delay(50);
        }
        Assert.Fail($"Timed out waiting for: {what}");
    }

    [Fact]
    public async Task HelloHandshake_Succeeds_EpochOne()
    {
        var device = await BindDeviceAsync();
        var client = new DeviceChannelClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var connected = new TaskCompletionSource<int>();
        client.OnConnected += epoch => connected.TrySetResult(epoch);
        var run = client.RunAsync(WsUri(device.DeviceId), device.DeviceAccess,
            device.DeviceId, device.Key, (body, _) => Task.FromResult(body), cts.Token);
        var epoch = await connected.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(1, epoch);
        Assert.True(client.Connected);
        cts.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task BadSignature_ClosesWithUnauthorized()
    {
        var device = await BindDeviceAsync();
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Bearer " + device.DeviceAccess);
        await socket.ConnectAsync(WsUri(device.DeviceId), CancellationToken.None);
        var challenge = await ChannelFraming.ReadMessageAsync(socket, CancellationToken.None);
        Assert.NotNull(challenge);
        // Sign with a DIFFERENT key than the bound one.
        var wrongKey = DeviceKeyPair.Generate();
        var nonce = ChannelEnvelope.TryParse(challenge!)!.Body.GetProperty("nonce_b64").GetString()!;
        var material = Encoding.UTF8.GetBytes(nonce + ":" + device.DeviceId);
        await ChannelFraming.SendAsync(socket, ChannelEnvelope.Create("hello", new
        {
            device_id = device.DeviceId,
            public_key_fingerprint = device.Key.Fingerprint,
            signature_b64 = Convert.ToBase64String(wrongKey.Sign(material)),
        }), CancellationToken.None);
        var error = await ChannelFraming.ReadMessageAsync(socket, CancellationToken.None);
        Assert.NotNull(error);
        var envelope = ChannelEnvelope.TryParse(error!);
        Assert.Equal("error", envelope!.Type);
        Assert.Equal("unauthorized", envelope.Body.GetProperty("code").GetString());
        var close = await ReadCloseAsync(socket);
        Assert.Equal(4401, (int)close!.Value);
    }

    [Fact]
    public async Task RevokedDevice_CannotConnect()
    {
        var device = await BindDeviceAsync();
        var http = NewHttpClient();
        http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", device.ControlAccess);
        using var revoke = await http.DeleteAsync("/api/v1/devices/" + device.DeviceId);
        Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);

        var client = new DeviceChannelClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ex = await Assert.ThrowsAsync<DeviceChannelClient.NonRetryableException>(() =>
            client.RunAsync(WsUri(device.DeviceId), device.DeviceAccess,
                device.DeviceId, device.Key, (body, _) => Task.FromResult(body), cts.Token));
        Assert.Contains("unauthorized", ex.Message);
    }

    [Fact]
    public async Task SecondConnection_EvictsFirst_EpochIncrements()
    {
        var device = await BindDeviceAsync();
        var first = new DeviceChannelClient();
        var second = new DeviceChannelClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var firstEpoch = new TaskCompletionSource<int>();
        var secondEpoch = new TaskCompletionSource<int>();
        first.OnConnected += e => firstEpoch.TrySetResult(e);
        second.OnConnected += e => secondEpoch.TrySetResult(e);
        var runFirst = first.RunAsync(WsUri(device.DeviceId), device.DeviceAccess,
            device.DeviceId, device.Key, (body, _) => Task.FromResult(body), cts.Token);
        Assert.Equal(1, await firstEpoch.Task.WaitAsync(TimeSpan.FromSeconds(15)));
        var runSecond = second.RunAsync(WsUri(device.DeviceId), device.DeviceAccess,
            device.DeviceId, device.Key, (body, _) => Task.FromResult(body), cts.Token);
        Assert.Equal(2, await secondEpoch.Task.WaitAsync(TimeSpan.FromSeconds(15)));
        // The first connection is evicted: it stops being connected.
        await WaitUntilAsync(() => !first.Connected, TimeSpan.FromSeconds(10), "first evicted");
        await runFirst.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(second.Connected);
        cts.Cancel();
        await runSecond.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Heartbeat_KeepsConnectionAlive()
    {
        var device = await BindDeviceAsync();
        var client = new DeviceChannelClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var connected = new TaskCompletionSource<int>();
        client.OnConnected += e => connected.TrySetResult(e);
        var run = client.RunAsync(WsUri(device.DeviceId), device.DeviceAccess,
            device.DeviceId, device.Key, (body, _) => Task.FromResult(body), cts.Token);
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await Task.Delay(TimeSpan.FromSeconds(7));
        Assert.True(client.Connected);
        cts.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Command_RoundTrip_Echo()
    {
        var device = await BindDeviceAsync();
        var client = new DeviceChannelClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var connected = new TaskCompletionSource<int>();
        client.OnConnected += e => connected.TrySetResult(e);
        var run = client.RunAsync(WsUri(device.DeviceId), device.DeviceAccess,
            device.DeviceId, device.Key, (body, _) => Task.FromResult(body), cts.Token);
        var epoch = await connected.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var (leaseId, leaseEpoch) = await AcquireLeaseAsync(device);
        Assert.Equal(epoch, leaseEpoch);

        var http = NewHttpClient();
        http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", device.ControlAccess);
        using var response = await http.PostAsJsonAsync(
            $"/api/v1/devices/{device.DeviceId}/commands",
            Envelope(device.DeviceId, leaseId, epoch, 1, "test.echo", new { ping = "hello-ws" }));
        var responseBody = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode,
            $"Command failed: {(int)response.StatusCode} {responseBody}");
        using var doc = JsonDocument.Parse(responseBody);
        Assert.Equal("succeeded", doc.RootElement.GetProperty("status").GetString());
        var result = doc.RootElement.GetProperty("result");
        Assert.Equal("test.echo", result.GetProperty("operation").GetString());
        Assert.Equal("hello-ws", result.GetProperty("payload").GetProperty("ping").GetString());
        cts.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Command_OfflineDevice_Returns503()
    {
        var device = await BindDeviceAsync();
        var client = new DeviceChannelClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var connected = new TaskCompletionSource<int>();
        client.OnConnected += e => connected.TrySetResult(e);
        var run = client.RunAsync(WsUri(device.DeviceId), device.DeviceAccess,
            device.DeviceId, device.Key, (body, _) => Task.FromResult(body), cts.Token);
        var epoch = await connected.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var (leaseId, _) = await AcquireLeaseAsync(device);
        cts.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(10));

        var http = NewHttpClient();
        http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", device.ControlAccess);
        using var response = await http.PostAsJsonAsync(
            $"/api/v1/devices/{device.DeviceId}/commands",
            Envelope(device.DeviceId, leaseId, epoch, 1, "test.echo", new { ping = "x" }));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("device_offline", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Command_WrongScope_Returns403()
    {
        var device = await BindDeviceAsync();
        // A control token with only devices.read cannot send commands.
        var email = NewEmail();
        var owner = await CreateUserAsync(email);
        var browser = NewHttpClient();
        await TestHelpers.LoginAsync(browser, email, Password);
        var readOnly = await AuthorizeAsync(browser, "muse-control", "devices.read");
        var http = NewHttpClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", readOnly);
        using var response = await http.PostAsJsonAsync(
            $"/api/v1/devices/{device.DeviceId}/commands",
            Envelope(device.DeviceId, Guid.NewGuid().ToString(), 1, 1, "test.echo", new { ping = "x" }));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("insufficient_scope", await response.Content.ReadAsStringAsync());
        _ = owner;
    }

    [Fact]
    public async Task Command_UnknownKind_Returns400()
    {
        var device = await BindDeviceAsync();
        var http = NewHttpClient();
        http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", device.ControlAccess);
        using var response = await http.PostAsJsonAsync(
            $"/api/v1/devices/{device.DeviceId}/commands",
            Envelope(device.DeviceId, Guid.NewGuid().ToString(), 1, 1, "teleport", new { }));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("unknown_operation", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Revoke_EvictsLiveConnection_AndReconnectFails()
    {
        var device = await BindDeviceAsync();
        var client = new DeviceChannelClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var connected = new TaskCompletionSource<int>();
        client.OnConnected += e => connected.TrySetResult(e);
        var run = client.RunAsync(WsUri(device.DeviceId), device.DeviceAccess,
            device.DeviceId, device.Key, (body, _) => Task.FromResult(body), cts.Token);
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(15));

        var http = NewHttpClient();
        http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", device.ControlAccess);
        using var revoke = await http.DeleteAsync("/api/v1/devices/" + device.DeviceId);
        Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);

        // The live connection is evicted with a non-retryable error.
        var ex = await Assert.ThrowsAsync<DeviceChannelClient.NonRetryableException>(() =>
            run.WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.Contains("revoked", ex.Message);
    }

    [Fact]
    public async Task OversizeFrame_ClosesConnection()
    {
        var device = await BindDeviceAsync();
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Bearer " + device.DeviceAccess);
        await socket.ConnectAsync(WsUri(device.DeviceId), CancellationToken.None);
        var challenge = await ChannelFraming.ReadMessageAsync(socket, CancellationToken.None);
        var nonce = ChannelEnvelope.TryParse(challenge!)!.Body.GetProperty("nonce_b64").GetString()!;
        var material = Encoding.UTF8.GetBytes(nonce + ":" + device.DeviceId);
        await ChannelFraming.SendAsync(socket, ChannelEnvelope.Create("hello", new
        {
            device_id = device.DeviceId,
            public_key_fingerprint = device.Key.Fingerprint,
            signature_b64 = Convert.ToBase64String(device.Key.Sign(material)),
        }), CancellationToken.None);
        var ack = await ChannelFraming.ReadMessageAsync(socket, CancellationToken.None);
        Assert.Equal("hello_ack", ChannelEnvelope.TryParse(ack!)!.Type);

        var big = new byte[1024 * 1024 + 1];
        Array.Fill(big, (byte)'x');
        await socket.SendAsync(big, WebSocketMessageType.Text, true, CancellationToken.None);
        var close = await ReadCloseAsync(socket);
        Assert.Equal(1009, (int)close!.Value);
    }

    [Fact]
    public async Task UnknownType_GetsErrorFrame_ConnectionStaysOpen()
    {
        var device = await BindDeviceAsync();
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Bearer " + device.DeviceAccess);
        await socket.ConnectAsync(WsUri(device.DeviceId), CancellationToken.None);
        var challenge = await ChannelFraming.ReadMessageAsync(socket, CancellationToken.None);
        var nonce = ChannelEnvelope.TryParse(challenge!)!.Body.GetProperty("nonce_b64").GetString()!;
        var material = Encoding.UTF8.GetBytes(nonce + ":" + device.DeviceId);
        await ChannelFraming.SendAsync(socket, ChannelEnvelope.Create("hello", new
        {
            device_id = device.DeviceId,
            public_key_fingerprint = device.Key.Fingerprint,
            signature_b64 = Convert.ToBase64String(device.Key.Sign(material)),
        }), CancellationToken.None);
        var ack = await ChannelFraming.ReadMessageAsync(socket, CancellationToken.None);
        Assert.Equal("hello_ack", ChannelEnvelope.TryParse(ack!)!.Type);

        await ChannelFraming.SendAsync(socket,
            ChannelEnvelope.Create("teleport", new { where = "moon" }), CancellationToken.None);
        var error = await ChannelFraming.ReadMessageAsync(socket, CancellationToken.None);
        var envelope = ChannelEnvelope.TryParse(error!);
        Assert.Equal("error", envelope!.Type);
        Assert.Equal("unknown_type", envelope.Body.GetProperty("code").GetString());

        await ChannelFraming.SendAsync(socket,
            ChannelEnvelope.Create("heartbeat", null, "hb-1"), CancellationToken.None);
        var heartbeatAck = await ChannelFraming.ReadMessageAsync(socket, CancellationToken.None);
        Assert.Equal("heartbeat_ack", ChannelEnvelope.TryParse(heartbeatAck!)!.Type);
    }

    private static async Task<WebSocketCloseStatus?> ReadCloseAsync(ClientWebSocket socket)
    {
        var buffer = new byte[1024];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await socket.ReceiveAsync(buffer, cts.Token);
        Assert.Equal(WebSocketMessageType.Close, result.MessageType);
        return result.CloseStatus;
    }
}
