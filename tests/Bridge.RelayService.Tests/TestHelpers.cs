using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Bridge.RelayService.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Bridge.RelayService.Tests;

/// <summary>
/// TestServer factory: fresh temp SQLite file per class, seeded clients.
/// In-memory transport (no sockets) — the IPv6/proxy quirk cannot bite.
/// </summary>
public sealed class RelayFactory : WebApplicationFactory<Program>
{
    public string DbPath { get; } = Path.Combine(
        Path.GetTempPath(), "bridge-relay-test-" + Guid.NewGuid().ToString("N") + ".db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            // Replace the DbContext registration outright: the config
            // override path proved unreliable under WebApplicationFactory
            // (the service kept the default relay.db in the bin dir).
            var descriptor = services.SingleOrDefault(
                d => d.ServiceType == typeof(DbContextOptions<ApplicationDbContext>));
            if (descriptor is not null)
                services.Remove(descriptor);
            services.AddDbContext<ApplicationDbContext>(options =>
            {
                options.UseSqlite($"Data Source={DbPath}");
                options.UseOpenIddict();
            });
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { File.Delete(DbPath); } catch { /* best effort */ }
        try { File.Delete(DbPath + "-wal"); } catch { /* best effort */ }
        try { File.Delete(DbPath + "-shm"); } catch { /* best effort */ }
    }
}

/// <summary>DelegatingHandler that persists cookies across requests.</summary>
public sealed class CookieHandler : DelegatingHandler
{
    private readonly CookieContainer _jar = new();
    public CookieHandler(HttpMessageHandler inner) : base(inner) { }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        if (request.RequestUri is not null)
        {
            var cookieHeader = _jar.GetCookieHeader(request.RequestUri);
            if (!string.IsNullOrEmpty(cookieHeader))
                request.Headers.Add("Cookie", cookieHeader);
        }
        var response = await base.SendAsync(request, ct);
        if (response.Headers.TryGetValues("Set-Cookie", out var setCookies))
            foreach (var value in setCookies)
                if (request.RequestUri is not null)
                    _jar.SetCookies(request.RequestUri, value);
        return response;
    }
}

public static class TestHelpers
{
    public static HttpClient CreateClient(RelayFactory factory)
    {
        var handler = new CookieHandler(factory.Server.CreateHandler());
        var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        return client;
    }

    public static async Task<ApplicationUser> CreateUserAsync(
        RelayFactory factory, string email, string password)
    {
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = new ApplicationUser { UserName = email, Email = email };
        var result = await users.CreateAsync(user, password);
        if (!result.Succeeded)
            throw new InvalidOperationException("CreateUser failed: " +
                string.Join(", ", result.Errors.Select(e => e.Description)));
        return user;
    }

    /// <summary>POST the Identity login form (handles the antiforgery token).</summary>
    public static async Task LoginAsync(HttpClient client, string email, string password)
    {
        var loginPage = await client.GetAsync("/Identity/Account/Login");
        loginPage.EnsureSuccessStatusCode();
        var html = await loginPage.Content.ReadAsStringAsync();
        var token = AntiforgeryToken(html);
        var form = new Dictionary<string, string>
        {
            ["Input.Email"] = email,
            ["Input.Password"] = password,
            ["Input.RememberMe"] = "false",
            ["__RequestVerificationToken"] = token,
        };
        using var response = await client.PostAsync("/Identity/Account/Login",
            new FormUrlEncodedContent(form));
        // Success: redirect to / (or to the 2FA page for 2FA users).
        Assert.True(response.StatusCode is HttpStatusCode.Found or HttpStatusCode.Redirect,
            $"Login did not redirect: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
    }

    public static string AntiforgeryToken(string html)
    {
        var m = Regex.Match(html,
            """name="__RequestVerificationToken"[^>]*value="([^"]+)""");
        if (!m.Success)
            throw new InvalidOperationException("Antiforgery token not found in page.");
        return WebUtility.HtmlDecode(m.Groups[1].Value);
    }

    public static (string Verifier, string Challenge) Pkce()
    {
        var verifierBytes = RandomNumberGenerator.GetBytes(48);
        var verifier = Base64UrlEncode(verifierBytes);
        var challenge = Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        return (verifier, challenge);
    }

    /// <summary>Submit the consent form like a browser: re-post the
    /// hidden authorize parameters from the consent page, plus the
    /// consent decision.</summary>
    public static async Task<HttpResponseMessage> SubmitConsentAsync(
        HttpClient client, string authUrl, string decision = "accept")
    {
        using var page = await client.GetAsync(authUrl);
        var html = await page.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("Allow access?", html);
        var fields = new Dictionary<string, string>();
        foreach (Match m in Regex.Matches(html,
            """<input type="hidden" name="([^"]+)" value="([^"]*)">"""))
            fields[WebUtility.HtmlDecode(m.Groups[1].Value)] =
                WebUtility.HtmlDecode(m.Groups[2].Value);
        fields["consent"] = decision;
        return await client.PostAsync("/connect/authorize",
            new FormUrlEncodedContent(fields));
    }

    public static string Base64UrlEncode(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>RFC 6238 TOTP (SHA-1, 30s step, 6 digits), as Identity's
    /// authenticator provider computes it.</summary>
    public static string Totp(string base32Secret, DateTimeOffset when)
    {
        var key = Base32Decode(base32Secret);
        var timestep = (long)(when.ToUnixTimeSeconds() / 30);
        var msg = BitConverter.GetBytes(IPAddress.HostToNetworkOrder(timestep));
        var hash = HMACSHA1.HashData(key, msg);
        var offset = hash[^1] & 0x0F;
        var code = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16)
            | (hash[offset + 2] << 8) | hash[offset + 3];
        return (code % 1_000_000).ToString("D6");
    }

    private static byte[] Base32Decode(string input)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var clean = input.TrimEnd('=').ToUpperInvariant();
        var output = new List<byte>();
        var buffer = 0;
        var bits = 0;
        foreach (var c in clean)
        {
            var value = alphabet.IndexOf(c);
            if (value < 0) throw new FormatException("Invalid base32 character.");
            buffer = (buffer << 5) | value;
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                output.Add((byte)(buffer >> bits));
            }
        }
        return output.ToArray();
    }
}
