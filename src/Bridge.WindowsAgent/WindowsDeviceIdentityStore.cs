using System.Security.Cryptography;
using System.Text.Json;
using Bridge.DeviceIdentity;
using Microsoft.Win32;

namespace Bridge.WindowsAgent;

/// <summary>
/// Windows device identity store (T10): the private key is encrypted
/// with DPAPI CurrentUser scope and kept in %LOCALAPPDATA%\Bridge\.
/// Only the current Windows user can decrypt it; the file is ACL'd to
/// that user by the OS profile. No key material in config, logs,
/// registry plaintext, CLI args, or chat. This class is Windows-only
/// and has never run on a real Windows machine yet (compile check
/// only on Linux).
/// </summary>
public sealed class WindowsDeviceIdentityStore : IDeviceIdentityStore
{
    private readonly string _path;

    public WindowsDeviceIdentityStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Bridge", "device-identity.bin");
    }

    public bool Exists => File.Exists(_path);

    public async Task<DeviceKeyPair?> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_path))
            return null;
        var protectedBytes = await File.ReadAllBytesAsync(_path, ct);
        var plain = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
        using var doc = JsonDocument.Parse(plain);
        var privateKey = Convert.FromBase64String(
            doc.RootElement.GetProperty("private_key_pkcs8").GetString()!);
        return DeviceKeyPair.FromPrivateKey(privateKey);
    }

    public async Task SaveAsync(DeviceKeyPair keyPair, CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(dir);
        var json = JsonSerializer.SerializeToUtf8Bytes(new
        {
            private_key_pkcs8 = Convert.ToBase64String(keyPair.PrivateKeyPkcs8),
            public_key_fingerprint = keyPair.Fingerprint,
        });
        var protectedBytes = ProtectedData.Protect(json, null, DataProtectionScope.CurrentUser);
        await File.WriteAllBytesAsync(_path, protectedBytes, ct);
    }

    public Task DeleteAsync(CancellationToken ct = default)
    {
        if (File.Exists(_path))
            File.Delete(_path);
        return Task.CompletedTask;
    }

    /// <summary>Stable machine id for the clone-detection fingerprint:
    /// the Windows machine GUID. Returns null when unreadable.</summary>
    public static string? GetMachineGuid()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Cryptography");
            return key?.GetValue("MachineGuid") as string;
        }
        catch
        {
            return null;
        }
    }
}
