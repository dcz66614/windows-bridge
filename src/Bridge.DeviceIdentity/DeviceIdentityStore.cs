using System.Text.Json;

namespace Bridge.DeviceIdentity;

/// <summary>
/// Where a device's private key lives. The interface is the same on
/// every platform; only the implementation differs:
/// - Windows: DPAPI (CurrentUser) protected file in %LOCALAPPDATA%
///   (see Bridge.WindowsAgent.WindowsDeviceIdentityStore; NOT_RUN here).
/// - Linux/dev: a 0600 file. DEV ONLY — never the Windows path.
/// Config and logs never contain key material.
/// </summary>
public interface IDeviceIdentityStore
{
    Task<DeviceKeyPair?> LoadAsync(CancellationToken ct = default);
    Task SaveAsync(DeviceKeyPair keyPair, CancellationToken ct = default);
    Task DeleteAsync(CancellationToken ct = default);
    bool Exists { get; }
}

/// <summary>
/// Dev/Linux file store. The file holds the PKCS#8 private key JSON
/// with 0600 permissions. This is NOT the Windows storage story
/// (DPAPI); it exists so the protocol can be tested on Linux.
/// </summary>
public sealed class FileDeviceIdentityStore : IDeviceIdentityStore
{
    private readonly string _path;

    public FileDeviceIdentityStore(string path) => _path = path;
    public bool Exists => File.Exists(_path);

    public async Task<DeviceKeyPair?> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_path))
            return null;
        var json = await File.ReadAllTextAsync(_path, ct);
        using var doc = JsonDocument.Parse(json);
        var privateKey = Convert.FromBase64String(
            doc.RootElement.GetProperty("private_key_pkcs8").GetString()!);
        return DeviceKeyPair.FromPrivateKey(privateKey);
    }

    public async Task SaveAsync(DeviceKeyPair keyPair, CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(_path));
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(new
        {
            private_key_pkcs8 = Convert.ToBase64String(keyPair.PrivateKeyPkcs8),
            public_key_fingerprint = keyPair.Fingerprint,
        });
        await File.WriteAllTextAsync(_path, json, ct);
#pragma warning disable CA1416 // Unix-only call; guarded by the catch below.
        try
        {
            File.SetUnixFileMode(_path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (PlatformNotSupportedException)
        {
            // Non-Unix platform: permissions are the store's job there.
        }
#pragma warning restore CA1416
    }

    public Task DeleteAsync(CancellationToken ct = default)
    {
        if (File.Exists(_path))
            File.Delete(_path);
        return Task.CompletedTask;
    }
}
