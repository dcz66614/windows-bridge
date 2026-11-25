using OpenIddict.Abstractions;

namespace Bridge.RelayService.Data;

/// <summary>
/// First-party clients + scopes (docs/04 §4.2). Idempotent: re-run
/// only adds what is missing. Client ids are public identifiers;
/// neither client carries a secret in v1 (public clients + PKCE /
/// device code). A confidential muse-control client is a deployment
/// decision for when the platform connector supports it.
/// </summary>
public static class SeedData
{
    public static readonly string[] Scopes =
    {
        "device.connect",   // device client: connect its own device, return results
        "devices.read",     // control client: list the account's devices
        "desktop.observe",  // control client: screenshot/UIA, after local approval
        "desktop.input",    // control client: inject input, after local approval
    };

    public static async Task EnsureSeededAsync(IServiceProvider services, CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        var scopeManager = scope.ServiceProvider.GetRequiredService<IOpenIddictScopeManager>();

        foreach (var name in Scopes)
        {
            if (await scopeManager.FindByNameAsync(name, ct) is null)
            {
                await scopeManager.CreateAsync(new OpenIddictScopeDescriptor
                {
                    Name = name,
                    Resources = { "bridge-relay" },
                }, ct);
            }
        }

        await EnsureClientAsync(manager,
            clientId: "windows-agent",
            displayName: "Bridge Windows Agent",
            grants: new[] { OpenIddictConstants.GrantTypes.AuthorizationCode, OpenIddictConstants.GrantTypes.DeviceCode, OpenIddictConstants.GrantTypes.RefreshToken },
            scopes: new[] { "device.connect" },
            redirectUris: new[] { "http://127.0.0.1/callback" },
            ct);

        await EnsureClientAsync(manager,
            clientId: "muse-control",
            displayName: "Muse Control Client",
            grants: new[] { OpenIddictConstants.GrantTypes.AuthorizationCode, OpenIddictConstants.GrantTypes.DeviceCode, OpenIddictConstants.GrantTypes.RefreshToken },
            scopes: new[] { "devices.read", "desktop.observe", "desktop.input" },
            redirectUris: new[] { "http://127.0.0.1/callback" },
            ct);
    }

    private static async Task EnsureClientAsync(
        IOpenIddictApplicationManager manager,
        string clientId, string displayName,
        string[] grants, string[] scopes, string[] redirectUris,
        CancellationToken ct)
    {
        if (await manager.FindByClientIdAsync(clientId, ct) is not null)
            return;

        var descriptor = new OpenIddictApplicationDescriptor
        {
            ClientId = clientId,
            DisplayName = displayName,
            ClientType = OpenIddictConstants.ClientTypes.Public,
            ConsentType = OpenIddictConstants.ConsentTypes.Explicit,
        };
        descriptor.Permissions.UnionWith(new[]
        {
            OpenIddictConstants.Permissions.Endpoints.Authorization,
            OpenIddictConstants.Permissions.Endpoints.Token,
            OpenIddictConstants.Permissions.Endpoints.DeviceAuthorization,
            OpenIddictConstants.Permissions.Endpoints.Revocation,
            OpenIddictConstants.Permissions.GrantTypes.AuthorizationCode,
            OpenIddictConstants.Permissions.GrantTypes.DeviceCode,
            OpenIddictConstants.Permissions.GrantTypes.RefreshToken,
            OpenIddictConstants.Permissions.ResponseTypes.Code,
            OpenIddictConstants.Permissions.Scopes.Profile,
            // Standard OIDC scope: a refresh token is only issued when
            // the client asks for offline_access.
            OpenIddictConstants.Permissions.Prefixes.Scope + OpenIddictConstants.Scopes.OfflineAccess,
        });
        // Least privilege: each client only gets its own scopes.
        foreach (var s in scopes)
            descriptor.Permissions.Add(OpenIddictConstants.Permissions.Prefixes.Scope + s);
        foreach (var uri in redirectUris)
            descriptor.RedirectUris.Add(new Uri(uri));
        descriptor.Requirements.UnionWith(new[]
        {
            OpenIddictConstants.Requirements.Features.ProofKeyForCodeExchange,
        });
        await manager.CreateAsync(descriptor, ct);
    }
}
