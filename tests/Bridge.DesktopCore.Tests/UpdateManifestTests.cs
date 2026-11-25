using System.Net;
using System.Security.Cryptography;
using System.Text;
using Bridge.DesktopCore;
using Xunit;

namespace Bridge.DesktopCore.Tests;

public sealed class UpdateManifestTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(_respond(request));
    }

    private static (string Json, ECDsa Public, byte[] Package) SignedManifest(
        string version = "1.1.0", string url = "https://updates.example.invalid/bridge/1.1.0.zip")
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var package = Encoding.UTF8.GetBytes("fake-package-bytes-1.1.0");
        var sha = Convert.ToHexString(SHA256.HashData(package)).ToLowerInvariant();
        var issued = new DateTimeOffset(2026, 11, 13, 6, 0, 0, TimeSpan.Zero);
        var signature = UpdateManifest.Sign(version, sha, package.Length, url, issued, key);
        var json = UpdateManifest.BuildManifestJson(version, sha, package.Length, url, issued, signature);
        var pub = ECDsa.Create(key.ExportParameters(false));
        return (json, pub, package);
    }

    [Fact]
    public void VerifyManifest_Valid_Passes_And_PackageMatches()
    {
        var (json, pub, package) = SignedManifest();
        var (manifest, error) = UpdateManifest.VerifyManifest(json, "updates.example.invalid", pub);
        Assert.Null(error);
        Assert.NotNull(manifest);
        Assert.Equal("1.1.0", manifest!.Version);
        Assert.Null(UpdateManifest.VerifyPackage(package, manifest));
    }

    [Fact]
    public void VerifyManifest_TamperedVersion_FailsSignature()
    {
        var (json, pub, _) = SignedManifest();
        var tampered = json.Replace("\"1.1.0\"", "\"9.9.9\"");
        Assert.Equal("signature_invalid",
            UpdateManifest.VerifyManifest(tampered, "updates.example.invalid", pub).Error);
    }

    [Fact]
    public void VerifyManifest_TamperedHash_FailsSignature()
    {
        var (json, pub, _) = SignedManifest();
        var tampered = json.Replace("a", "b", StringComparison.Ordinal);
        var (_, error) = UpdateManifest.VerifyManifest(tampered, "updates.example.invalid", pub);
        Assert.NotNull(error);
    }

    [Fact]
    public void VerifyManifest_WrongKey_FailsSignature()
    {
        var (json, _, _) = SignedManifest();
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.Equal("signature_invalid",
            UpdateManifest.VerifyManifest(json, "updates.example.invalid", other).Error);
    }

    [Fact]
    public void VerifyManifest_NonHttps_Or_WrongHost_Rejected()
    {
        var (jsonHttp, pub, _) = SignedManifest(url: "http://updates.example.invalid/bridge/1.1.0.zip");
        Assert.Equal("update_source_mismatch",
            UpdateManifest.VerifyManifest(jsonHttp, "updates.example.invalid", pub).Error);
        var (jsonOther, _, _) = SignedManifest(url: "https://evil.example.invalid/bridge/1.1.0.zip");
        Assert.Equal("update_source_mismatch",
            UpdateManifest.VerifyManifest(jsonOther, "updates.example.invalid", pub).Error);
    }

    [Fact]
    public void VerifyPackage_Size_And_Hash_Mismatch()
    {
        var (json, pub, package) = SignedManifest();
        var (manifest, _) = UpdateManifest.VerifyManifest(json, "updates.example.invalid", pub);
        Assert.Equal("package_size_mismatch",
            UpdateManifest.VerifyPackage([1, 2, 3], manifest!));
        var wrong = (byte[])package.Clone();
        wrong[0] ^= 0xFF;
        Assert.Equal("package_hash_mismatch", UpdateManifest.VerifyPackage(wrong, manifest!));
    }

    [Fact]
    public void IsNewerThanInstalled_RejectsDowngrade_And_Same()
    {
        Assert.True(UpdateManifest.IsNewerThanInstalled("1.1.0", "1.0.0"));
        Assert.False(UpdateManifest.IsNewerThanInstalled("1.0.0", "1.0.0"));
        Assert.False(UpdateManifest.IsNewerThanInstalled("0.9.0", "1.0.0"));
        Assert.True(UpdateManifest.IsNewerThanInstalled("1.0.0", null));
    }

    [Fact]
    public async Task DownloadPackage_FollowsSameHostRedirect_RejectsOtherHost()
    {
        var (json, pub, package) = SignedManifest();
        var (manifest, _) = UpdateManifest.VerifyManifest(json, "updates.example.invalid", pub);
        using var http = new HttpClient(new StubHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("1.1.0.zip") &&
                string.IsNullOrEmpty(req.RequestUri.Query))
            {
                var r = new HttpResponseMessage(HttpStatusCode.Redirect);
                r.Headers.Location = new Uri("https://updates.example.invalid/bridge/1.1.0.zip?v=2");
                return r;
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(package),
            };
        }));
        // The stub returns redirect responses directly (no automatic
        // redirect following), which is exactly the behavior the
        // production caller gets with AllowAutoRedirect = false.
        var (bytes, error) = await UpdateManifest.DownloadPackageAsync(http, manifest!);
        Assert.Null(error);
        Assert.Equal(package, bytes);

        using var evil = new HttpClient(new StubHandler(_ =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.Redirect);
            r.Headers.Location = new Uri("https://evil.example.invalid/pwn.zip");
            return r;
        }));
        var (_, evilError) = await UpdateManifest.DownloadPackageAsync(evil, manifest!);
        Assert.Equal("redirect_to_other_host", evilError);
    }
}
