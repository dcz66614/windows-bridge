using System.Security.Cryptography;

namespace Bridge.DeviceIdentity;

/// <summary>
/// A device's long-lived identity: an ECDSA P-256 keypair generated
/// locally BEFORE any login (T10). The private key never leaves the
/// device; the server stores only the public key + its SHA-256
/// fingerprint. Signatures prove possession at the WSS handshake (T11).
/// </summary>
public sealed class DeviceKeyPair
{
    public required byte[] PublicKeyDer { get; init; }
    public required byte[] PrivateKeyPkcs8 { get; init; }

    /// <summary>SHA-256 of the DER public key, lowercase hex.</summary>
    public string Fingerprint => Convert.ToHexString(SHA256.HashData(PublicKeyDer)).ToLowerInvariant();

    public static DeviceKeyPair Generate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return new DeviceKeyPair
        {
            PublicKeyDer = key.ExportSubjectPublicKeyInfo(),
            PrivateKeyPkcs8 = key.ExportPkcs8PrivateKey(),
        };
    }

    public static DeviceKeyPair FromPrivateKey(byte[] pkcs8)
    {
        using var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(pkcs8, out _);
        return new DeviceKeyPair
        {
            PublicKeyDer = key.ExportSubjectPublicKeyInfo(),
            PrivateKeyPkcs8 = pkcs8,
        };
    }

    public static DeviceKeyPair FromPublicKey(byte[] der) => new()
    {
        PublicKeyDer = der,
        PrivateKeyPkcs8 = Array.Empty<byte>(),
    };

    /// <summary>Sign a server nonce (challenge-response, T11).</summary>
    public byte[] Sign(byte[] nonce)
    {
        using var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(PrivateKeyPkcs8, out _);
        return key.SignData(nonce, HashAlgorithmName.SHA256);
    }

    /// <summary>Verify a signature against this public key.</summary>
    public bool Verify(byte[] nonce, byte[] signature)
    {
        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(PublicKeyDer, out _);
        return key.VerifyData(nonce, signature, HashAlgorithmName.SHA256);
    }
}

/// <summary>
/// Stable per-machine fingerprint for clone detection (T10). The
/// server refuses to bind an existing device_id to a different
/// fingerprint: a cloned VM or a copied identity file must re-bind
/// as a NEW device, never silently share one.
/// </summary>
public static class MachineFingerprint
{
    /// <summary>
    /// Best-effort stable fingerprint from durable machine facts.
    /// Windows: the machine GUID from the registry (the agent passes
    /// it in); Linux/dev: machine-id. Never a MAC address alone.
    /// </summary>
    public static string Compute(string machineId, string osDescription)
    {
        var material = machineId.Trim().ToLowerInvariant() + "|" + osDescription.Trim();
        return Convert.ToHexString(SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
    }
}
