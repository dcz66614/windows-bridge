using System.Net;
using Bridge.RelayService.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Bridge.RelayService.Tests;

/// <summary>
/// T09 first auth path: password + TOTP authenticator, end to end.
/// The TOTP code is computed from the same shared secret Identity
/// stores (RFC 6238), exactly as an authenticator app would.
/// </summary>
public sealed class TwoFactorTests : IClassFixture<RelayFactory>
{
    private readonly RelayFactory _factory;
    private const string Password = "Correct-Horse-99!";

    public TwoFactorTests(RelayFactory factory) => _factory = factory;

    [Fact]
    public async Task PasswordPlusTotpLogsInAndAuthorizes()
    {
        var email = "totp-" + Guid.NewGuid().ToString("N")[..8] + "@example.test";
        var user = await TestHelpers.CreateUserAsync(_factory, email, Password);
        string secret;
        using (var scope = _factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var u = await users.FindByIdAsync(user.Id)
                ?? throw new InvalidOperationException("User not found.");
            await users.ResetAuthenticatorKeyAsync(u);
            secret = await users.GetAuthenticatorKeyAsync(u)
                ?? throw new InvalidOperationException("No authenticator key.");
            // Prove the code verifies, then enable 2FA.
            var code = TestHelpers.Totp(secret, DateTimeOffset.UtcNow);
            Assert.True(await users.VerifyTwoFactorTokenAsync(
                u, TokenOptions.DefaultAuthenticatorProvider, code));
            var enabled = await users.SetTwoFactorEnabledAsync(u, true);
            Assert.True(enabled.Succeeded);
        }

        var client = TestHelpers.CreateClient(_factory);
        var loginPage = await client.GetAsync("/Identity/Account/Login");
        loginPage.EnsureSuccessStatusCode();
        var token = TestHelpers.AntiforgeryToken(await loginPage.Content.ReadAsStringAsync());
        using var login = await client.PostAsync("/Identity/Account/Login",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["Input.Email"] = email,
                ["Input.Password"] = Password,
                ["Input.RememberMe"] = "false",
                ["__RequestVerificationToken"] = token,
            }));
        Assert.Equal(HttpStatusCode.Found, login.StatusCode);
        var location = login.Headers.Location!.ToString();
        Assert.Contains("LoginWith2fa", location);

        // Wrong code is rejected; right code logs in.
        var twofaPage = await client.GetAsync("/Identity/Account/LoginWith2fa");
        twofaPage.EnsureSuccessStatusCode();
        var token2 = TestHelpers.AntiforgeryToken(await twofaPage.Content.ReadAsStringAsync());
        using var wrong = await client.PostAsync("/Identity/Account/LoginWith2fa",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["Input.TwoFactorCode"] = "000000",
                ["Input.RememberMachine"] = "false",
                ["__RequestVerificationToken"] = token2,
            }));
        Assert.Equal(HttpStatusCode.OK, wrong.StatusCode); // stays on the page with an error

        var goodCode = TestHelpers.Totp(secret, DateTimeOffset.UtcNow);
        using var right = await client.PostAsync("/Identity/Account/LoginWith2fa",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["Input.TwoFactorCode"] = goodCode,
                ["Input.RememberMachine"] = "false",
                ["__RequestVerificationToken"] = token2,
            }));
        Assert.Equal(HttpStatusCode.Found, right.StatusCode);
        Assert.DoesNotContain("LoginWith2fa", right.Headers.Location!.ToString());

        // The 2FA'd session can authorize an OAuth client.
        var (_, challenge) = TestHelpers.Pkce();
        using var authorize = await client.GetAsync(
            "/connect/authorize?response_type=code&client_id=muse-control"
            + "&redirect_uri=http://127.0.0.1/callback&scope=devices.read"
            + $"&code_challenge={challenge}&code_challenge_method=S256&state=t1");
        Assert.Equal(HttpStatusCode.OK, authorize.StatusCode);
        Assert.Contains("Allow access?", await authorize.Content.ReadAsStringAsync());
    }
}
