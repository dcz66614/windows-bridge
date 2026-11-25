using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Bridge.DesktopCore;

/// <summary>
/// Update manifest verification (T26). Signature verification and
/// package-hash verification are separate steps, exactly as the
/// plan requires:
///   1. The manifest is signed (ECDSA P-256 + SHA-256, IEEE P1363
///      signature, base64) over a FIXED field order canonical JSON.
///      The download URL must be HTTPS and its host must equal the
///      configured update origin exactly (fixed update source; no
///      redirect to another host is followed).
///   2. The downloaded package is checked by size + SHA-256.
///   3. The manifest version must be newer than the installed
///      version (anti-downgrade / anti-rollback).
/// The public key is a parameter: the release pipeline (T28)
/// generates the keypair offline, ships the public key with the
/// installer, and never puts the private key in the repo. No
/// update is applied from an unverified remote script.
/// </summary>
public static class UpdateManifest
{
    public sealed record Manifest(
        string Version,
        string PackageSha256,
        long PackageSize,
        string DownloadUrl,
        DateTimeOffset IssuedAt,
        string SignatureB64);

    public static string CanonicalJson(
        string version, string packageSha256, long packageSize,
        string downloadUrl, DateTimeOffset issuedAt)
    {
        // Fixed field order, compact, no whitespace. Both the
        // signer and the verifier build exactly these bytes.
        return "{\"version\":" + JsonSerializer.Serialize(version) +
               ",\"package_sha256\":" + JsonSerializer.Serialize(packageSha256) +
               ",\"package_size\":" + packageSize.ToString(System.Globalization.CultureInfo.InvariantCulture) +
               ",\"download_url\":" + JsonSerializer.Serialize(downloadUrl) +
               ",\"issued_at\":" + JsonSerializer.Serialize(issuedAt.ToString("o")) + "}";
    }

    /// <summary>Sign a manifest (release tooling only; test helper here).</summary>
    public static string Sign(
        string version, string packageSha256, long packageSize,
        string downloadUrl, DateTimeOffset issuedAt, ECDsa privateKey)
    {
        var bytes = Encoding.UTF8.GetBytes(
            CanonicalJson(version, packageSha256, packageSize, downloadUrl, issuedAt));
        return Convert.ToBase64String(privateKey.SignData(bytes, HashAlgorithmName.SHA256));
    }

    /// <summary>Build the manifest JSON for publication.</summary>
    public static string BuildManifestJson(
        string version, string packageSha256, long packageSize,
        string downloadUrl, DateTimeOffset issuedAt, string signatureB64)
    {
        var json = CanonicalJson(version, packageSha256, packageSize, downloadUrl, issuedAt);
        return json[..^1] + ",\"signature_b64\":" + JsonSerializer.Serialize(signatureB64) + "}";
    }

    /// <summary>
    /// Verify a manifest: structure, fixed-source URL, version
    /// format, and signature. Returns the parsed manifest or an
    /// error. Package bytes are NOT checked here (see VerifyPackage).
    /// </summary>
    public static (Manifest? Result, string? Error) VerifyManifest(
        string manifestJson, string updateOrigin, ECDsa publicKey)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(manifestJson);
        }
        catch (JsonException)
        {
            return (null, "manifest_malformed");
        }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return (null, "manifest_malformed");
            string? version = GetString(root, "version");
            string? sha = GetString(root, "package_sha256");
            string? url = GetString(root, "download_url");
            string? signature = GetString(root, "signature_b64");
            if (version is null || sha is null || url is null || signature is null ||
                !root.TryGetProperty("package_size", out var sizeEl) ||
                !sizeEl.TryGetInt64(out var size) ||
                !root.TryGetProperty("issued_at", out var issuedEl) ||
                !DateTimeOffset.TryParse(issuedEl.GetString(), out var issuedAt))
                return (null, "manifest_malformed");
            if (sha.Length != 64 || !sha.All(c => Uri.IsHexDigit(c)))
                return (null, "manifest_malformed");
            if (size < 0)
                return (null, "manifest_malformed");

            // Fixed update source: HTTPS, exact host match.
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                uri.Scheme != "https" ||
                !string.Equals(uri.Host, updateOrigin, StringComparison.OrdinalIgnoreCase))
                return (null, "update_source_mismatch");

            var canonical = CanonicalJson(version, sha, size, url, issuedAt);
            byte[] signatureBytes;
            try
            {
                signatureBytes = Convert.FromBase64String(signature);
            }
            catch (FormatException)
            {
                return (null, "manifest_malformed");
            }
            var ok = publicKey.VerifyData(
                Encoding.UTF8.GetBytes(canonical), signatureBytes,
                HashAlgorithmName.SHA256);
            if (!ok)
                return (null, "signature_invalid");
            return (new Manifest(version, sha.ToLowerInvariant(), size, url, issuedAt, signature), null);
        }
    }

    /// <summary>Verify downloaded package bytes against the manifest.</summary>
    public static string? VerifyPackage(byte[] packageBytes, Manifest manifest)
    {
        if (packageBytes.LongLength != manifest.PackageSize)
            return "package_size_mismatch";
        var sha = Convert.ToHexString(SHA256.HashData(packageBytes)).ToLowerInvariant();
        if (sha != manifest.PackageSha256)
            return "package_hash_mismatch";
        return null;
    }

    /// <summary>Anti-downgrade: the candidate must be strictly newer.</summary>
    public static bool IsNewerThanInstalled(string candidateVersion, string? installedVersion)
    {
        if (installedVersion is null)
            return true;
        return InstallLayout.CompareVersions(candidateVersion, installedVersion) > 0;
    }

    /// <summary>
    /// Download a package from the manifest URL. Redirects are
    /// refused unless the target stays on the same HTTPS host.
    /// CALLER REQUIREMENT: the HttpClient must be created with
    /// automatic redirects DISABLED (e.g. a SocketsHttpHandler
    /// with AllowAutoRedirect = false); otherwise the handler
    /// follows redirects before this code can inspect them.
    /// Returns bytes or an error.
    /// </summary>
    public static async Task<(byte[]? Bytes, string? Error)> DownloadPackageAsync(
        HttpClient http, Manifest manifest, CancellationToken ct = default)
    {
        var current = manifest.DownloadUrl;
        for (var hops = 0; hops < 5; hops++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            using var response = await http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                var location = response.Headers.Location;
                if (location is null)
                    return (null, "redirect_without_location");
                var next = location.IsAbsoluteUri
                    ? location
                    : new Uri(new Uri(current), location);
                var originHost = new Uri(manifest.DownloadUrl).Host;
                if (next.Scheme != "https" ||
                    !string.Equals(next.Host, originHost, StringComparison.OrdinalIgnoreCase))
                    return (null, "redirect_to_other_host");
                current = next.ToString();
                continue;
            }
            if (!response.IsSuccessStatusCode)
                return (null, "download_failed_" + (int)response.StatusCode);
            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            return (bytes, null);
        }
        return (null, "too_many_redirects");
    }

    private static string? GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;
}
