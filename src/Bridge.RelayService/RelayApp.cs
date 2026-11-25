using Bridge.RelayService.Data;
using Bridge.RelayService.DeviceChannel;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;

namespace Bridge.RelayService;

/// <summary>
/// The relay application factory. Program.cs is a thin wrapper around
/// this so tests can build the same app on a real Kestrel socket
/// (WebSocket tests need a real transport; TestServer cannot upgrade).
/// </summary>
public static class RelayApp
{
    public static WebApplication CreateApp(
        string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        configure?.Invoke(builder);

        // ---- configuration ----
        // RelayService:DatabasePath (default relay.db next to the binary),
        // RelayService:Urls (default http://127.0.0.1:5000 loopback only).
        var dbPath = builder.Configuration["RelayService:DatabasePath"]
            ?? Path.Combine(AppContext.BaseDirectory, "relay.db");
        var urls = builder.Configuration["RelayService:Urls"] ?? "http://127.0.0.1:5000";
        builder.WebHost.UseUrls(urls);

        // DataProtection keys persist in the data directory next to
        // the database. Without this, a restart (or a container
        // rebuild) invalidates every cookie and antiforgery token.
        // The key directory is part of the backup set (see deploy/).
        var dataDir = Path.GetDirectoryName(Path.GetFullPath(dbPath))!;
        builder.Services.AddDataProtection()
            .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDir, "keys")))
            .SetApplicationName("bridge-relay");

        // Behind the TLS-terminating reverse proxy (see deploy/):
        // trust X-Forwarded-For/Proto from the loopback proxy only,
        // so OpenIddict issuer URLs and redirect URIs use the
        // public HTTPS origin instead of http://127.0.0.1:5000.
        builder.Services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor
                | ForwardedHeaders.XForwardedProto;
            options.KnownProxies.Add(System.Net.IPAddress.Loopback);
            options.KnownProxies.Add(System.Net.IPAddress.IPv6Loopback);
        });

        builder.Services.AddDbContext<ApplicationDbContext>(options =>
        {
            options.UseSqlite($"Data Source={dbPath}");
            options.UseOpenIddict();
        });

        // Identity: password + TOTP authenticator (AddDefaultTokenProviders).
        // First complete path is password + TOTP; passkeys are a later step (T09).
        builder.Services.AddDefaultIdentity<ApplicationUser>(options =>
        {
            options.SignIn.RequireConfirmedAccount = false;
            options.Password.RequiredLength = 10;
            options.Password.RequireDigit = true;
            options.Password.RequireLowercase = true;
            options.Password.RequireUppercase = true;
            options.Password.RequireNonAlphanumeric = true;
        })
        .AddEntityFrameworkStores<ApplicationDbContext>()
        .AddDefaultTokenProviders();

        builder.Services.AddAuthentication();

        builder.Services.AddOpenIddict()
            .AddCore(options =>
            {
                options.UseEntityFrameworkCore()
                    .UseDbContext<ApplicationDbContext>();
            })
            .AddServer(options =>
            {
                options.SetAuthorizationEndpointUris("/connect/authorize")
                    .SetTokenEndpointUris("/connect/token")
                    .SetDeviceAuthorizationEndpointUris("/connect/deviceauthorization")
                    .SetEndUserVerificationEndpointUris("/connect/verification")
                    .SetRevocationEndpointUris("/connect/revocation")
                    .SetUserInfoEndpointUris("/connect/userinfo");

                options.AllowAuthorizationCodeFlow()
                    .RequireProofKeyForCodeExchange();
                options.AllowDeviceAuthorizationFlow();
                options.AllowRefreshTokenFlow();

                options.RegisterScopes(SeedData.Scopes);
                options.RegisterClaims(OpenIddictConstants.Claims.Subject);

                // Lifetimes are config-visible (docs/04 §4.3): access 10 min,
                // refresh 14 days. Rolling refresh tokens + reuse detection are
                // OpenIddict's default (DisableRollingRefreshTokens opts out).
                // Reuse leeway is zero by deliberate decision: a refresh token
                // presented twice is a replay, full stop — no grace window for
                // concurrent retries to hide in (clients re-authenticate).
                options.SetAccessTokenLifetime(TimeSpan.FromMinutes(10));
                options.SetRefreshTokenLifetime(TimeSpan.FromDays(14));
                options.SetRefreshTokenReuseLeeway(TimeSpan.Zero);

                // Signing/encryption certificates. Development uses
                // ephemeral development certificates. Production MUST
                // load the operator's PFX (see the fail-closed check
                // below): the file's mere existence is not enough —
                // an earlier version of this code checked the path
                // but never loaded the certificate, so every restart
                // would have minted a fresh signing key and
                // invalidated all tokens.
                if (builder.Environment.IsProduction())
                {
                    var certPath = builder.Configuration["RelayService:SigningCertificate:Path"];
                    if (string.IsNullOrWhiteSpace(certPath) || !File.Exists(certPath))
                        throw new InvalidOperationException(
                            "Production requires RelayService:SigningCertificate:Path (PFX). " +
                            "Development certificates are never used in Production.");
                    var certPassword = builder.Configuration["RelayService:SigningCertificate:Password"];
                    var cert = new System.Security.Cryptography.X509Certificates.X509Certificate2(
                        certPath, certPassword);
                    options.AddSigningCertificate(cert)
                        .AddEncryptionCertificate(cert);
                }
                else
                {
                    options.AddDevelopmentEncryptionCertificate()
                        .AddDevelopmentSigningCertificate();
                }

                var aspNetCore = options.UseAspNetCore()
                    .EnableAuthorizationEndpointPassthrough()
                    .EnableTokenEndpointPassthrough()
                    .EnableEndUserVerificationEndpointPassthrough()
                    .EnableStatusCodePagesIntegration();

                // Local development and TestServer run over plain HTTP loopback.
                // Production MUST serve HTTPS (reverse proxy or Kestrel cert);
                // this escape hatch is Development-only by construction.
                if (builder.Environment.IsDevelopment())
                    aspNetCore.DisableTransportSecurityRequirement();
            })
            .AddValidation(options =>
            {
                options.UseLocalServer();
                options.UseAspNetCore();
            });

        // Explicit application part: under a test host the entry
        // assembly is the test assembly, so the service's compiled
        // Razor views (_LoginPartial) would not be discovered.
        // In production the entry assembly IS this assembly (no-op).
        builder.Services.AddControllersWithViews()
            .AddApplicationPart(typeof(RelayApp).Assembly);
        builder.Services.AddRazorPages();
        builder.Services.AddSingleton<PairingRateLimiter>();
        builder.Services.AddSingleton<DeviceConnectionRegistry>();
        builder.Services.AddSingleton<CommandDispatcher>();
        builder.Services.AddScoped<LeaseManager>();
        builder.Services.AddHostedService<DeviceChannelSweeper>();

        // Scope policies (docs/04 §4.2). OpenIddict emits scopes as one
        // space-separated "scope" claim, so the check splits on spaces.
        builder.Services.AddAuthorizationBuilder()
            .AddPolicy("DevicesRead", policy => policy.RequireAssertion(ctx =>
                ctx.User.FindAll("scope")
                    .SelectMany(c => c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    .Contains("devices.read")))
            .AddPolicy("DeviceConnect", policy => policy.RequireAssertion(ctx =>
                ctx.User.FindAll("scope")
                    .SelectMany(c => c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    .Contains("device.connect")))
            .AddPolicy("DesktopObserve", policy => policy.RequireAssertion(ctx =>
                ctx.User.FindAll("scope")
                    .SelectMany(c => c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    .Contains("desktop.observe")))
            .AddPolicy("DesktopInput", policy => policy.RequireAssertion(ctx =>
                ctx.User.FindAll("scope")
                    .SelectMany(c => c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    .Contains("desktop.input")));

        var app = builder.Build();

        // Production must not run on development certificates: fail closed
        // unless a signing certificate is configured.
        if (app.Environment.IsProduction())
        {
            var certPath = builder.Configuration["RelayService:SigningCertificate:Path"];
            if (string.IsNullOrWhiteSpace(certPath) || !File.Exists(certPath))
                throw new InvalidOperationException(
                    "Production requires RelayService:SigningCertificate:Path (PFX). " +
                    "Development certificates are never used in Production.");
        }

        if (app.Environment.IsDevelopment())
        {
            app.UseDeveloperExceptionPage();
        }

        app.UseForwardedHeaders();
        app.UseWebSockets();
        app.UseStaticFiles();
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();

        app.MapRazorPages();
        app.MapControllers();
        app.Map("/ws/device", DeviceWebSocketEndpoint.HandleAsync);

        // Health: no user information, just service version + liveness.
        app.MapGet("/healthz", () => Results.Json(new
        {
            service = "bridge-relay",
            version = "0.1.0-t11",
            status = "ok",
        }));

        return app;
    }

    public static async Task InitializeAsync(WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.Database.EnsureCreated();
        await SeedData.EnsureSeededAsync(app.Services);
    }
}
