using System.Text.Json;

namespace Bridge.WindowsAgent.Install;

/// <summary>
/// Per-user agent config (T25). Lives at %LOCALAPPDATA%\Bridge\config.json.
/// Only the local user (tray UI / installer) edits it; the relay and
/// Muse never see it. `file_root` is null by default: file access is
/// closed until the user authorizes a directory.
/// </summary>
public sealed record AgentConfig
{
    public string Version { get; init; } = Installer.ProductVersion;
    public bool Autostart { get; init; } = true;
    public string? FileRoot { get; init; }
    public string? RelayOrigin { get; init; }
    public DateTimeOffset InstalledAt { get; init; } = DateTimeOffset.UtcNow;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
    };

    public static AgentConfig Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var config = JsonSerializer.Deserialize<AgentConfig>(File.ReadAllText(path), Json);
                if (config is not null)
                    return config;
            }
        }
        catch
        {
            // Corrupt config: fall back to defaults, never crash startup.
        }
        return new AgentConfig();
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, Json));
        File.Move(temp, path, overwrite: true);
    }
}
