using Bridge.DesktopCore;
using Xunit;

namespace Bridge.DesktopCore.Tests;

public sealed class InstallLayoutTests
{
    private static string TempRoot()
    {
        var dir = Path.Combine(Path.GetTempPath(),
            "bridge-install-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string Payload()
    {
        var dir = Path.Combine(Path.GetTempPath(),
            "bridge-payload-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Bridge.WindowsAgent.exe"), "agent-bytes");
        File.WriteAllText(Path.Combine(dir, "Bridge.DesktopWorker.exe"), "worker-bytes");
        Directory.CreateDirectory(Path.Combine(dir, "runtimes"));
        File.WriteAllText(Path.Combine(dir, "runtimes", "core.dll"), "runtime-bytes");
        return dir;
    }

    [Fact]
    public void Install_VerifyRoundTrip()
    {
        var root = TempRoot();
        var layout = new InstallLayout(root);
        var (manifest, error) = layout.InstallFrom(Payload(), "1.0.0");
        Assert.Null(error);
        Assert.NotNull(manifest);
        Assert.Equal("1.0.0", layout.CurrentVersion());
        Assert.Empty(layout.Verify("1.0.0"));
        Assert.Equal(3, manifest!.Files.Count);
    }

    [Fact]
    public void Install_TamperedFile_FailsVerify()
    {
        var root = TempRoot();
        var layout = new InstallLayout(root);
        Assert.Null(layout.InstallFrom(Payload(), "1.0.0").Error);
        File.WriteAllText(Path.Combine(root, "app", "1.0.0", "Bridge.WindowsAgent.exe"), "tampered");
        var problems = layout.Verify("1.0.0");
        Assert.Contains(problems, p => p.StartsWith("hash_mismatch", StringComparison.Ordinal));
        File.WriteAllText(Path.Combine(root, "app", "1.0.0", "extra.dll"), "x");
        Assert.Contains(layout.Verify("1.0.0"), p => p.StartsWith("unexpected", StringComparison.Ordinal));
    }

    [Fact]
    public void Install_SameVersion_Refused_Downgrade_Refused()
    {
        var root = TempRoot();
        var layout = new InstallLayout(root);
        Assert.Null(layout.InstallFrom(Payload(), "1.0.0").Error);
        Assert.Equal("already_installed", layout.InstallFrom(Payload(), "1.0.0").Error);
        Assert.Equal("downgrade_refused", layout.InstallFrom(Payload(), "0.9.0").Error);
        Assert.Null(layout.InstallFrom(Payload(), "1.1.0").Error);
        Assert.Equal("1.1.0", layout.CurrentVersion());
        Assert.Empty(layout.Verify("1.1.0"));
        // The old version stays on disk for rollback.
        Assert.True(Directory.Exists(Path.Combine(root, "app", "1.0.0")));
    }

    [Fact]
    public void CompareVersions_Numeric()
    {
        Assert.True(InstallLayout.CompareVersions("1.10.0", "1.9.0") > 0);
        Assert.True(InstallLayout.CompareVersions("2.0.0", "1.99.0") > 0);
        Assert.Equal(0, InstallLayout.CompareVersions("1.0.0", "1.0.0"));
        Assert.True(InstallLayout.CompareVersions("1.0.0", "1.0.1") < 0);
    }

    [Fact]
    public void RemoveVersion_And_ResidueCheck()
    {
        var root = TempRoot();
        var layout = new InstallLayout(root);
        Assert.Null(layout.InstallFrom(Payload(), "1.0.0").Error);
        Assert.True(layout.RemoveVersion("1.0.0"));
        Assert.False(Directory.Exists(Path.Combine(root, "app", "1.0.0")));
        File.WriteAllText(Path.Combine(root, "stray.txt"), "x");
        Directory.CreateDirectory(Path.Combine(root, "app", "1.0.0.tmp-abc12345"));
        var residue = layout.ResidueCheck();
        Assert.Contains(residue, p => p.StartsWith("unexpected_file", StringComparison.Ordinal));
        Assert.Contains(residue, p => p.StartsWith("stale_staging", StringComparison.Ordinal));
    }

    [Fact]
    public void Install_MissingSource_FailsCleanly()
    {
        var layout = new InstallLayout(TempRoot());
        var (manifest, error) = layout.InstallFrom(Path.Combine(Path.GetTempPath(), "no-such-dir"), "1.0.0");
        Assert.Null(manifest);
        Assert.Equal("source_missing", error);
        Assert.Null(layout.CurrentVersion());
    }
}
