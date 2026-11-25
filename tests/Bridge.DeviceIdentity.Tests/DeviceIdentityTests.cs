using System.Text;
using Bridge.DeviceIdentity;

namespace Bridge.DeviceIdentity.Tests;

public sealed class DeviceIdentityTests
{
    [Fact]
    public void KeyPair_Generate_Sign_Verify_RoundTrip()
    {
        var key = DeviceKeyPair.Generate();
        Assert.Equal(64, key.Fingerprint.Length);
        var nonce = Encoding.UTF8.GetBytes("server-challenge-123");
        var sig = key.Sign(nonce);
        Assert.True(key.Verify(nonce, sig));
        Assert.False(key.Verify(Encoding.UTF8.GetBytes("other-nonce"), sig));
        // A public-only copy verifies but cannot sign.
        var pub = DeviceKeyPair.FromPublicKey(key.PublicKeyDer);
        Assert.True(pub.Verify(nonce, sig));
        Assert.Equal(key.Fingerprint, pub.Fingerprint);
        Assert.ThrowsAny<Exception>(() => pub.Sign(nonce));
    }

    [Fact]
    public void KeyPair_Generate_IsRandom()
    {
        var a = DeviceKeyPair.Generate();
        var b = DeviceKeyPair.Generate();
        Assert.NotEqual(a.Fingerprint, b.Fingerprint);
    }

    [Fact]
    public void MachineFingerprint_IsDeterministic_AndMachineBound()
    {
        var f1 = MachineFingerprint.Compute("machine-guid-1", "Windows 11");
        var f2 = MachineFingerprint.Compute("machine-guid-1", "Windows 11");
        var f3 = MachineFingerprint.Compute("machine-guid-2", "Windows 11");
        Assert.Equal(f1, f2);
        Assert.NotEqual(f1, f3);
        Assert.Equal(64, f1.Length);
    }

    [Fact]
    public async Task FileStore_RoundTrip_PreservesKey_AndIsPrivate()
    {
        var path = Path.Combine(Path.GetTempPath(),
            "bridge-test-identity-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var store = new FileDeviceIdentityStore(path);
            Assert.False(store.Exists);
            Assert.Null(await store.LoadAsync());
            var key = DeviceKeyPair.Generate();
            await store.SaveAsync(key);
            Assert.True(store.Exists);
            var loaded = await store.LoadAsync();
            Assert.NotNull(loaded);
            Assert.Equal(key.Fingerprint, loaded!.Fingerprint);
            var nonce = Encoding.UTF8.GetBytes("n");
            Assert.True(loaded.Verify(nonce, key.Sign(nonce)));
#pragma warning disable CA1416 // Unix-only call; the test host is Linux.
            var mode = File.GetUnixFileMode(path);
#pragma warning restore CA1416
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
            await store.DeleteAsync();
            Assert.False(store.Exists);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}
