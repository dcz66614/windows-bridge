using System.Security.Claims;
using Bridge.RelayService.Data;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Bridge.RelayService.Controllers;

/// <summary>
/// OpenIddict authorize/token/device/revocation endpoints (T09).
/// Follows the OpenIddict server pattern: the framework validates the
/// protocol (PKCE, codes, grants); this controller only decides identity
/// (who is logged in) and consent (what the user approved).
/// Login itself is ASP.NET Core Identity (password + TOTP); no login
/// fields are ever logged.
/// </summary>
public sealed class AuthorizationController : Controller
{
    private readonly SignInManager<ApplicationUser> _signInManager;
    private readonly UserManager<ApplicationUser> _userManager;

    public AuthorizationController(
        SignInManager<ApplicationUser> signInManager,
        UserManager<ApplicationUser> userManager)
    {
        _signInManager = signInManager;
        _userManager = userManager;
    }

    [HttpGet("~/connect/authorize")]
    [HttpPost("~/connect/authorize")]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> Authorize()
    {
        var request = HttpContext.GetOpenIddictServerRequest() ??
            throw new InvalidOperationException("The OpenID Connect request cannot be retrieved.");

        var auth = await HttpContext.AuthenticateAsync(IdentityConstants.ApplicationScheme);
        if (!auth.Succeeded)
        {
            // Not logged in: send the user to Identity's login page, then
            // back here. ReturnUrl keeps the authorize request intact.
            return Challenge(new AuthenticationProperties
            {
                RedirectUri = Request.Path + Request.QueryString,
            }, IdentityConstants.ApplicationScheme);
        }

        var user = await _userManager.GetUserAsync(auth.Principal)
            ?? throw new InvalidOperationException("The user cannot be retrieved.");

        // Explicit consent (seeded ConsentType.Explicit): v1 shows the
        // consent form on every authorize request. No stored-consent
        // shortcut yet — the form is the decision point and is exercised
        // in tests via prompt=consent.
        if (Request.Method == "GET")
            return ConsentForm(request);
        if (!string.Equals(Request.Form["consent"], "accept", StringComparison.Ordinal))
            return Forbid(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);

        var identity = new ClaimsIdentity(
            TokenValidationParameters.DefaultAuthenticationType,
            Claims.Name, Claims.Role);
        identity.AddClaim(Claims.Subject, await _userManager.GetUserIdAsync(user));
        identity.AddClaim(Claims.Name, await _userManager.GetUserNameAsync(user)
            ?? throw new InvalidOperationException("The user has no name."));
        identity.AddClaim(Claims.Email, await _userManager.GetEmailAsync(user) ?? "");

        var principal = new ClaimsPrincipal(identity);
        principal.SetScopes(request.GetScopes());
        principal.SetResources("bridge-relay");
        foreach (var claim in principal.Claims)
            claim.SetDestinations(Destinations.AccessToken, Destinations.IdentityToken);

        return SignIn(principal, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    [HttpPost("~/connect/token")]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> Exchange()
    {
        var request = HttpContext.GetOpenIddictServerRequest() ??
            throw new InvalidOperationException("The OpenID Connect request cannot be retrieved.");

        if (request.IsAuthorizationCodeGrantType() || request.IsRefreshTokenGrantType())
        {
            var result = await HttpContext.AuthenticateAsync(
                OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
            var principal = result.Principal ??
                throw new InvalidOperationException("The token request cannot be authenticated.");
            return SignIn(principal, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        }

        if (request.IsDeviceCodeGrantType())
        {
            var result = await HttpContext.AuthenticateAsync(
                OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
            var principal = result.Principal ??
                throw new InvalidOperationException("The device code cannot be authenticated.");
            // The verification principal carries no scopes (the user only
            // approved a user code). Fill them from the device code's own
            // authorization, created at deviceauthorization time with the
            // scopes the device originally asked for.
            var scopes = await GetScopesForDeviceCodeAsync(request.DeviceCode!);
            if (scopes.Length > 0)
                principal.SetScopes(scopes);
            return SignIn(principal, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        }

        throw new InvalidOperationException("The specified grant type is not supported.");
    }

    [HttpGet("~/connect/verification")]
    public IActionResult VerificationForm([FromQuery] string? user_code)
    {
        if (string.IsNullOrWhiteSpace(user_code))
            return Content(VerificationHtml(null), "text/html");
        return Content(VerificationHtml(user_code), "text/html");
    }

    [HttpPost("~/connect/verification")]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> Verify()
    {
        var request = HttpContext.GetOpenIddictServerRequest();
        var userCode = Request.Form["user_code"].ToString();
        if (request is null || string.IsNullOrWhiteSpace(userCode))
            return BadRequest("Missing user code.");

        var auth = await HttpContext.AuthenticateAsync(IdentityConstants.ApplicationScheme);
        if (!auth.Succeeded)
        {
            return Challenge(new AuthenticationProperties
            {
                RedirectUri = "/connect/verification?user_code=" + Uri.EscapeDataString(userCode),
            }, IdentityConstants.ApplicationScheme);
        }

        // Approve the pending device authorization for this user. The
        // device flow's principal carries the device code; signing it in
        // with the user's identity completes the grant.
        var user = await _userManager.GetUserAsync(auth.Principal)
            ?? throw new InvalidOperationException("The user cannot be retrieved.");
        var identity = new ClaimsIdentity(
            TokenValidationParameters.DefaultAuthenticationType,
            Claims.Name, Claims.Role);
        identity.AddClaim(Claims.Subject, await _userManager.GetUserIdAsync(user));
        identity.AddClaim(Claims.Name, await _userManager.GetUserNameAsync(user) ?? "");
        var principal = new ClaimsPrincipal(identity);
        principal.SetResources("bridge-relay");
        foreach (var claim in principal.Claims)
            claim.SetDestinations(Destinations.AccessToken, Destinations.IdentityToken);
        return SignIn(principal, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    // NOTE: /connect/revocation is intentionally NOT implemented here:
    // revocation passthrough is off, so OpenIddict serves it natively
    // (validates the token/client and revokes it).

    /// <summary>
    /// Scopes of the authorization behind a device code. The device code
    /// token's ReferenceId IS the device code; its authorization holds
    /// the scopes requested at deviceauthorization time.
    /// </summary>
    private async Task<string[]> GetScopesForDeviceCodeAsync(string deviceCode)
    {
        try
        {
            var tokenManager = HttpContext.RequestServices
                .GetRequiredService<IOpenIddictTokenManager>();
            var authorizationManager = HttpContext.RequestServices
                .GetRequiredService<IOpenIddictAuthorizationManager>();
            var token = await tokenManager.FindByReferenceIdAsync(deviceCode);
            if (token is null)
                return Array.Empty<string>();
            var authorizationId = await tokenManager.GetAuthorizationIdAsync(token);
            if (authorizationId is null)
                return Array.Empty<string>();
            var authorization = await authorizationManager.FindByIdAsync(authorizationId);
            if (authorization is null)
                return Array.Empty<string>();
            return (await authorizationManager.GetScopesAsync(authorization)).ToArray();
        }
        catch
        {
            // Scope recovery is best-effort: a failed lookup must not
            // block the token exchange itself.
            return Array.Empty<string>();
        }
    }

    private IActionResult ConsentForm(OpenIddictRequest request)
    {
        var scopes = string.Join(", ", request.GetScopes());
        // The authorize POST is validated from the FORM body, not the
        // query string: the original parameters must be re-posted as
        // hidden fields, exactly as a browser form would.
        var hidden = new System.Text.StringBuilder();
        foreach (var (key, values) in Request.Query)
            foreach (var value in values)
                hidden.Append("<input type=\"hidden\" name=\"")
                    .Append(System.Net.WebUtility.HtmlEncode(key))
                    .Append("\" value=\"")
                    .Append(System.Net.WebUtility.HtmlEncode(value))
                    .Append("\">");
        var html = $"""
            <!doctype html><html><body>
            <h1>Allow access?</h1>
            <p>Client <b>{System.Net.WebUtility.HtmlEncode(request.ClientId)}</b>
            requests scopes: <b>{System.Net.WebUtility.HtmlEncode(scopes)}</b>.</p>
            <form method="post" action="/connect/authorize">
            {hidden}
            <button type="submit" name="consent" value="accept">Allow</button>
            <button type="submit" name="consent" value="deny">Deny</button>
            </form></body></html>
            """;
        return Content(html, "text/html");
    }

    private static string VerificationHtml(string? userCode) => $"""
        <!doctype html><html><body>
        <h1>Device verification</h1>
        <form method="post" action="/connect/verification">
        <label>User code: <input name="user_code" value="{System.Net.WebUtility.HtmlEncode(userCode ?? "")}"></label>
        <button type="submit">Verify device</button>
        </form></body></html>
        """;
}
