using System.Security.Cryptography;
using System.Text.Json;

namespace Bridge.DesktopCore;

/// <summary>
/// Install layout for the per-user, no-admin installer (T25).
/// OS-neutral: the install root is supplied by the caller (on
/// Windows: %LOCALAPPDATA%\Bridge). Layout:
///   &lt;root&gt;/app/&lt;version&gt;/...      versioned payload (agent + worker + runtime)
///   &lt;root&gt;/app/current.txt     version of the live install
///   &lt;root&gt;/app/&lt;version&gt;/install.json   install manifest (hashes)
///   &lt;root&gt;/config.json        user config (autostart, file root)
///   &lt;root&gt;/data/             runtime data (journal, identity)
/// The Windows agent adds registry Run key + Start Menu shortcut
/// on top of this layout; those are Windows-only and live there.
/// </summary>
public sealed class InstallLayout
{
    public string Root { get; }
    public string AppRoot => Path.Combine(Root, "app");
    public string CurrentFile => Path.Combine(AppRoot, "current.txt");
    public string ConfigFile => Path.Combine(Root, "config.json");
    public string DataDir => Path.Combine(Root, "data");

    public InstallLayout(string installRoot)
    {
        Root = installRoot;
    }

    public string AppDir(string version) => Path.Combine(AppRoot, version);
    public string ManifestFile(string version) => Path.Combine(AppDir(version), "install.json");

    public sealed record ManifestEntry(string RelativePath, string Sha256, long ByteLength);
    public sealed record Manifest(string Version, DateTimeOffset InstalledAt, IReadOnlyList<ManifestEntry> Files);

    /// <summary>Compare dotted versions numerically. Returns &gt;0 when a &gt; b.</summary>
    public static int CompareVersions(string a, string b)
    {
        var pa = a.Split('.');
        var pb = b.Split('.');
        for (var i = 0; i < Math.Max(pa.Length, pb.Length); i++)
        {
            var na = i < pa.Length && int.TryParse(pa[i], out var va) ? va : 0;
            var nb = i < pb.Length && int.TryParse(pb[i], out var vb) ? vb : 0;
            if (na != nb)
                return na.CompareTo(nb);
        }
        return 0;
    }

    public string? CurrentVersion()
    {
        try
        {
            if (!File.Exists(CurrentFile))
                return null;
            var text = File.ReadAllText(CurrentFile).Trim();
            return string.IsNullOrEmpty(text) ? null : text;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Install a payload directory as a new version.</summary>
    public (Manifest? Result, string? Error) InstallFrom(string sourceDir, string version)
    {
        if (!Directory.Exists(sourceDir))
            return (null, "source_missing");
        var target = AppDir(version);
        if (Directory.Exists(target))
            return (null, "already_installed");
        var current = CurrentVersion();
        if (current is not null && CompareVersions(version, current) < 0)
            return (null, "downgrade_refused");
        Directory.CreateDirectory(AppRoot);
        var staging = AppDir(version + ".tmp-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            CopyDirectory(sourceDir, staging);
            var manifest = ComputeManifest(staging, version);
            // Write the manifest into staging BEFORE the move; the
            // target directory does not exist yet.
            WriteManifest(manifest, staging);
            Directory.Move(staging, target);
            WriteCurrent(version);
            return (manifest, null);
        }
        catch (Exception ex)
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); } catch { }
            return (null, "install_failed: " + ex.Message);
        }
    }

    public Manifest ComputeManifest(string appDir, string version)
    {
        var entries = new List<ManifestEntry>();
        foreach (var file in Directory.EnumerateFiles(appDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(appDir, file);
            if (relative.Equals("install.json", StringComparison.OrdinalIgnoreCase))
                continue;
            var bytes = File.ReadAllBytes(file);
            entries.Add(new ManifestEntry(
                relative,
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                bytes.Length));
        }
        entries.Sort((x, y) => string.CompareOrdinal(x.RelativePath, y.RelativePath));
        return new Manifest(version, DateTimeOffset.UtcNow, entries);
    }

    public void WriteManifest(Manifest manifest, string appDir)
    {
        var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(appDir, "install.json"), json);
    }

    public Manifest? ReadManifest(string version)
    {
        try
        {
            var json = File.ReadAllText(ManifestFile(version));
            return JsonSerializer.Deserialize<Manifest>(json);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Verify a version against its manifest. Returns problems, empty = OK.</summary>
    public IReadOnlyList<string> Verify(string version)
    {
        var problems = new List<string>();
        var manifest = ReadManifest(version);
        if (manifest is null)
            return ["manifest_missing"];
        var dir = AppDir(version);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in manifest.Files)
        {
            var file = Path.Combine(dir, entry.RelativePath);
            seen.Add(entry.RelativePath);
            if (!File.Exists(file))
            {
                problems.Add($"missing: {entry.RelativePath}");
                continue;
            }
            var bytes = File.ReadAllBytes(file);
            var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (sha != entry.Sha256)
                problems.Add($"hash_mismatch: {entry.RelativePath}");
            else if (bytes.Length != entry.ByteLength)
                problems.Add($"size_mismatch: {entry.RelativePath}");
        }
        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(dir, file);
            if (relative.Equals("install.json", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!seen.Contains(relative))
                problems.Add($"unexpected: {relative}");
        }
        return problems;
    }

    public void WriteCurrent(string version)
    {
        var temp = CurrentFile + ".tmp";
        File.WriteAllText(temp, version);
        File.Move(temp, CurrentFile, overwrite: true);
    }

    public bool RemoveVersion(string version)
    {
        try
        {
            var dir = AppDir(version);
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>List unexpected entries directly under the root (residue check).</summary>
    public IReadOnlyList<string> ResidueCheck()
    {
        var problems = new List<string>();
        if (!Directory.Exists(Root))
            return problems;
        var knownDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "app", "data" };
        var knownFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "config.json" };
        foreach (var dir in Directory.EnumerateDirectories(Root))
            if (!knownDirs.Contains(Path.GetFileName(dir)))
                problems.Add($"unexpected_dir: {Path.GetFileName(dir)}");
        foreach (var file in Directory.EnumerateFiles(Root))
            if (!knownFiles.Contains(Path.GetFileName(file)))
                problems.Add($"unexpected_file: {Path.GetFileName(file)}");
        if (Directory.Exists(AppRoot))
        {
            foreach (var dir in Directory.EnumerateDirectories(AppRoot))
            {
                var name = Path.GetFileName(dir);
                if (name.Contains(".tmp-", StringComparison.Ordinal))
                    problems.Add($"stale_staging: {name}");
            }
        }
        return problems;
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var dest = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest);
        }
    }
}
