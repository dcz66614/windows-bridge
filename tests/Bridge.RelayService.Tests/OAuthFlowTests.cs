using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Web;
using Xunit;

namespace Bridge.RelayService.Tests;

/// <summary>
/// T09 end-to-end: discovery, auth code + PKCE, device flow, refresh
/// rotation + reuse detection, revocation, scope enforcement. All flows
/// run against the real service through TestServer (no mocks).
/// </summary>
public sealed class OAuthFlowTests : IClassFixture<RelayFactory>
{
    private readonly RelayFactory _factory;
    private const string Password = "Correct-Horse-99!";

    private static string NewEmail() => "sam-" + Guid.NewGuid().ToString("N")[..8] + "@example.test";

    public OAuthFlowTests(RelayFactory factory) => _factory = factory;

    private static string Q(string url, string name)
    {
        var uri = new Uri(url);
        return HttpUtility.ParseQueryString(uri.Query)[name]
            ?? throw new InvalidOperationException($"Missing {name} in {url}.");
    }

    private async Task<(HttpClient Client, string Access, string Refresh)> LoginAndAuthorizeAsync(
        string clientId, string scope, string redirectUri = "http://127.0.0.1/callback")
    {
        var email = NewEmail();
        await TestHelpers.CreateUserAsync(_factory, email, Password);
        var client = TestHelpers.CreateClient(_factory);
        await TestHelpers.LoginAsync(client, email, Password);
        var (verifier, challenge) = TestHelpers.Pkce();
        var state = "state-" + Guid.NewGuid().ToString("N")[..8];
        var authUrl = "/connect/authorize?response_type=code"
            + $"&client_id={clientId}&redirect_uri={Uri.EscapeDataString(redirectUri)}"
            + $"&scope={Uri.EscapeDataString(scope + " offline_access")}"
            + $"&code_challenge={challenge}&code_challenge_method=S256&state={state}";
        using var authorized = await TestHelpers.SubmitConsentAsync(client, authUrl);
        var authBody = await authorized.Content.ReadAsStringAsync();
        Assert.True(authorized.StatusCode == HttpStatusCode.Found,
            $"Authorize POST returned {(int)authorized.StatusCode}: {authBody}");
        var location = authorized.Headers.Location!.ToString();
        Assert.StartsWith(redirectUri, location);
        var code = Q(location, "code");
        Assert.Equal(state, Q(location, "state"));
        using var token = await client.PostAsync("/connect/token", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = redirectUri,
                ["client_id"] = clientId,
                ["code_verifier"] = verifier,
            }));
        var body = await token.Content.ReadAsStringAsync();
        Assert.True(token.IsSuccessStatusCode, $"Token exchange failed: {(int)token.StatusCode} {body}");
        using var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.TryGetProperty("access_token", out var accessEl),
            $"No access_token in token response: {body}");
        var access = accessEl.GetString()!;
        Assert.True(doc.RootElement.TryGetProperty("refresh_token", out var refreshEl),
            $"No refresh_token in token response: {body}");
        var refresh = refreshEl.GetString()!;
        Assert.InRange(doc.RootElement.GetProperty("expires_in").GetInt32(), 590, 600);
        return (client, access, refresh);
    }

    [Fact]
    public async Task DiscoveryListsEndpoints()
    {
        var client = TestHelpers.CreateClient(_factory);
        using var response = await client.GetAsync("/.well-known/openid-configuration");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.EndsWith("/connect/authorize", root.GetProperty("authorization_endpoint").GetString());
        Assert.EndsWith("/connect/token", root.GetProperty("token_endpoint").GetString());
        Assert.EndsWith("/connect/deviceauthorization",
            root.GetProperty("device_authorization_endpoint").GetString());
        Assert.EndsWith("/connect/revocation", root.GetProperty("revocation_endpoint").GetString());
        Assert.EndsWith("/connect/userinfo", root.GetProperty("userinfo_endpoint").GetString());
    }

    [Fact]
    public async Task HealthzIsAnonymousAndMinimal()
    {
        var client = TestHelpers.CreateClient(_factory);
        using var response = await client.GetAsync("/healthz");
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"status\":\"ok\"", body);
        Assert.DoesNotContain("example.test", body);
    }

    [Fact]
    public async Task AuthorizationCodeFlowIssuesScopedTokens()
    {
        var (_, access, _) = await LoginAndAuthorizeAsync("muse-control", "devices.read");
        var client = TestHelpers.CreateClient(_factory);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", access);
        using var devices = await client.GetAsync("/api/v1/devices");
        var body = await devices.Content.ReadAsStringAsync();
        Assert.True(devices.StatusCode == HttpStatusCode.OK, $"devices 500 body: {body[..Math.Min(800, body.Length)]}");
        Assert.Contains("\"devices\":[]", body);
    }

    [Fact]
    public async Task ApiRequiresAuthentication()
    {
        var client = TestHelpers.CreateClient(_factory);
        using var response = await client.GetAsync("/api/v1/devices");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task DeviceScopeCannotReadDevices()
    {
        var (_, access, _) = await LoginAndAuthorizeAsync("windows-agent", "device.connect");
        var client = TestHelpers.CreateClient(_factory);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", access);
        using var response = await client.GetAsync("/api/v1/devices");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task PkceMismatchIsRejected()
    {
        var email = NewEmail();
        await TestHelpers.CreateUserAsync(_factory, email, Password);
        var client = TestHelpers.CreateClient(_factory);
        await TestHelpers.LoginAsync(client, email, Password);
        var (_, challenge) = TestHelpers.Pkce();
        var authUrl = "/connect/authorize?response_type=code"
            + "&client_id=muse-control&redirect_uri=http://127.0.0.1/callback"
            + "&scope=devices.read"
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
                ["code_verifier"] = "wrong-verifier-that-does-not-match-the-challenge",
            }));
        Assert.Equal(HttpStatusCode.BadRequest, token.StatusCode);
        var body = await token.Content.ReadAsStringAsync();
        Assert.Contains("invalid_grant", body);
    }

    [Fact]
    public async Task DeviceFlowEndToEnd()
    {
        var client = TestHelpers.CreateClient(_factory);
        using var deviceAuth = await client.PostAsync("/connect/deviceauthorization",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = "windows-agent",
                ["scope"] = "device.connect",
            }));
        var deviceBody = await deviceAuth.Content.ReadAsStringAsync();
        Assert.True(deviceAuth.IsSuccessStatusCode, $"Device auth failed: {(int)deviceAuth.StatusCode} {deviceBody}");
        using var deviceDoc = JsonDocument.Parse(deviceBody);
        var deviceCode = deviceDoc.RootElement.GetProperty("device_code").GetString()!;
        var userCode = deviceDoc.RootElement.GetProperty("user_code").GetString()!;
        Assert.Contains("/connect/verification",
            deviceDoc.RootElement.GetProperty("verification_uri").GetString());

        // Polling before approval: authorization_pending.
        using var pending = await client.PostAsync("/connect/token", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                ["device_code"] = deviceCode,
                ["client_id"] = "windows-agent",
            }));
        Assert.Equal(HttpStatusCode.BadRequest, pending.StatusCode);
        Assert.Contains("authorization_pending", await pending.Content.ReadAsStringAsync());

        // User approves the user code in the browser.
        var email = NewEmail();
        await TestHelpers.CreateUserAsync(_factory, email, Password);
        await TestHelpers.LoginAsync(client, email, Password);
        using var verifyPage = await client.GetAsync(
            "/connect/verification?user_code=" + Uri.EscapeDataString(userCode));
        Assert.Equal(HttpStatusCode.OK, verifyPage.StatusCode);
        using var verified = await client.PostAsync("/connect/verification",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["user_code"] = userCode }));
        var verifiedBody = await verified.Content.ReadAsStringAsync();
        Assert.True((int)verified.StatusCode is >= 200 and < 400,
            $"Verification failed: {(int)verified.StatusCode} {verifiedBody}");

        // Polling after approval: tokens with the device scope.
        using var token = await client.PostAsync("/connect/token", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                ["device_code"] = deviceCode,
                ["client_id"] = "windows-agent",
            }));
        var tokenBody = await token.Content.ReadAsStringAsync();
        Assert.True(token.IsSuccessStatusCode, $"Device token failed: {(int)token.StatusCode} {tokenBody}");
        using var tokenDoc = JsonDocument.Parse(tokenBody);
        Assert.True(tokenDoc.RootElement.TryGetProperty("scope", out var scopeEl),
            $"No scope in device token response: {tokenBody}");
        Assert.Equal("device.connect", scopeEl.GetString());
    }

    [Fact]
    public async Task RefreshRotatesAndReuseIsDetected()
    {
        var (client, _, refresh1) = await LoginAndAuthorizeAsync("muse-control", "devices.read");
        using var rotated = await client.PostAsync("/connect/token", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refresh1,
                ["client_id"] = "muse-control",
            }));
        var rotatedBody = await rotated.Content.ReadAsStringAsync();
        Assert.True(rotated.IsSuccessStatusCode, $"Refresh failed: {(int)rotated.StatusCode} {rotatedBody}");
        using var rotatedDoc = JsonDocument.Parse(rotatedBody);
        var refresh2 = rotatedDoc.RootElement.GetProperty("refresh_token").GetString()!;
        Assert.NotEqual(refresh1, refresh2);

        // Reusing the old refresh token must fail (rotation).
        using var reuse = await client.PostAsync("/connect/token", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refresh1,
                ["client_id"] = "muse-control",
            }));
        Assert.Equal(HttpStatusCode.BadRequest, reuse.StatusCode);
        Assert.Contains("invalid_grant", await reuse.Content.ReadAsStringAsync());

        // Reuse detection revokes the whole chain: the rotated token dies too.
        // (With the default reuse leeway this was flaky — a fast reuse
        // slipped through; leeway is zero in Program.cs, so the chain
        // revocation is now deterministic.)
        using var afterReuse = await client.PostAsync("/connect/token", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refresh2,
                ["client_id"] = "muse-control",
            }));
        Assert.Equal(HttpStatusCode.BadRequest, afterReuse.StatusCode);
    }

    [Fact]
    public async Task RevocationKillsRefreshToken()
    {
        var (client, _, refresh) = await LoginAndAuthorizeAsync("muse-control", "devices.read");
        using var revoked = await client.PostAsync("/connect/revocation", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["token"] = refresh,
                ["token_type_hint"] = "refresh_token",
                ["client_id"] = "muse-control",
            }));
        Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);
        using var refreshAttempt = await client.PostAsync("/connect/token", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refresh,
                ["client_id"] = "muse-control",
            }));
        Assert.Equal(HttpStatusCode.BadRequest, refreshAttempt.StatusCode);
    }
}
