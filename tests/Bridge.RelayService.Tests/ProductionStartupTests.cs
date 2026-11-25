using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Bridge.RelayService;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Bridge.RelayService.Tests;

/// <summary>
/// Production startup gates (T27). An earlier version of RelayApp
/// checked that the signing PFX existed but never loaded it, so
/// every restart would have minted a fresh signing key and
/// invalidated all tokens. These tests pin the fixed behavior:
/// no PFX -> fail closed; a PFX with the wrong password -> fail;
/// a valid PFX -> the app starts and answers /healthz.
/// </summary>
public sealed class ProductionStartupTests
{
    private static string MakePfx(string password)
    {
        var path = Path.Combine(Path.GetTempPath(),
            "bridge-test-cert-" + Guid.NewGuid().ToString("N") + ".pfx");
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=bridge-relay-test", rsa, HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        using var cert = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        File.WriteAllBytes(path, cert.Export(X509ContentType.Pfx, password));
        return path;
    }

    private static (WebApplication App, string DbPath) BuildProductionApp(string? certPath, string? certPassword)
    {
        var dbPath = Path.Combine(Path.GetTempPath(),
            "bridge-relay-prod-" + Guid.NewGuid().ToString("N") + ".db");
        var app = RelayApp.CreateApp(new[] { "--environment", "Production" }, builder =>
        {
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RelayService:DatabasePath"] = dbPath,
                ["RelayService:Urls"] = "http://127.0.0.1:0",
                ["RelayService:SigningCertificate:Path"] = certPath,
                ["RelayService:SigningCertificate:Password"] = certPassword,
            });
        });
        return (app, dbPath);
    }

    [Fact]
    public void Production_WithoutSigningCertificate_FailsClosed()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            BuildProductionApp(null, null));
        Assert.Contains("SigningCertificate", ex.Message);
    }

    [Fact]
    public void Production_WithWrongPassword_Fails()
    {
        var pfx = MakePfx("correct-password");
        Assert.ThrowsAny<CryptographicException>(() =>
            BuildProductionApp(pfx, "wrong-password"));
    }

    [Fact]
    public async Task Production_WithValidPfx_Starts_And_AnswersHealthz()
    {
        var pfx = MakePfx("correct-password");
        var (app, dbPath) = BuildProductionApp(pfx, "correct-password");
        await RelayApp.InitializeAsync(app);
        await app.StartAsync();
        try
        {
            var baseUrl = app.Urls.First(u => u.StartsWith("http://127.0.0.1", StringComparison.Ordinal));
            using var http = new HttpClient();
            using var response = await http.GetAsync(baseUrl + "/healthz");
            Assert.True(response.IsSuccessStatusCode,
                $"healthz failed: {(int)response.StatusCode}");
            // Force a DataProtection operation: the key ring must
            // then be persisted next to the database (restart
            // survival for cookies and antiforgery tokens).
            var protector = app.Services
                .GetRequiredService<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>()
                .CreateProtector("test");
            _ = protector.Protect(System.Text.Encoding.UTF8.GetBytes("probe"));
            Assert.True(Directory.Exists(Path.Combine(
                Path.GetDirectoryName(dbPath)!, "keys")),
                "DataProtection keys directory was not created.");
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
