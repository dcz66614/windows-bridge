using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Bridge.DesktopCore;
using Microsoft.Win32;

namespace Bridge.WindowsAgent.Install;

/// <summary>
/// Per-user, no-admin installer (T25). The published agent exe IS
/// the installer: run from anywhere it copies its own directory
/// into %LOCALAPPDATA%\Bridge\app\&lt;version&gt;, writes the config,
/// creates a Start Menu shortcut and (if chosen) a HKCU Run key,
/// then launches the installed copy. No admin, no LocalSystem
/// service, no console window. Upgrade keeps the previous version
/// on disk until the new one reports healthy; failure restores
/// the old shortcut/Run targets. Uninstall removes registry +
/// shortcuts and schedules the install directory for deletion
/// after this process exits.
/// Signing is NOT done here: the executables and the distribution
/// ZIP must be signed in the release pipeline (T28); SmartScreen
/// reputation cannot be guaranteed by signing alone.
/// </summary>
[SupportedOSPlatform("windows")]
public static class Installer
{
    public const string ProductVersion = "0.1.0";
    public const string AppName = "Bridge";
    public const string RunKeyName = "Bridge";

    public static string InstallRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Bridge");

    public static string StartMenuShortcut =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            "Programs", "Windows Bridge.lnk");

    public static bool IsInstalledRun(string exePath, string installRoot)
    {
        var appRoot = Path.Combine(installRoot, "app") + Path.DirectorySeparatorChar;
        return exePath.StartsWith(appRoot, StringComparison.OrdinalIgnoreCase);
    }

    public static string InstalledExePath(InstallLayout layout, string version) =>
        Path.Combine(layout.AppDir(version), "Bridge.WindowsAgent.exe");

    /// <summary>Full first install. Returns an error string, or null on success.</summary>
    public static string? Install(string sourceDir, InstallLayout layout, bool autostart, string? fileRoot)
    {
        var (manifest, error) = layout.InstallFrom(sourceDir, ProductVersion);
        if (error is not null)
            return error;
        _ = manifest;
        var config = new AgentConfig
        {
            Version = ProductVersion,
            Autostart = autostart,
            FileRoot = fileRoot,
            InstalledAt = DateTimeOffset.UtcNow,
        };
        config.Save(layout.ConfigFile);
        var exe = InstalledExePath(layout, ProductVersion);
        try
        {
            CreateShortcut(StartMenuShortcut, exe);
            SetAutostart(autostart, exe);
        }
        catch (Exception ex)
        {
            return "shortcut_or_startup_failed: " + ex.Message;
        }
        return null;
    }

    /// <summary>
    /// Upgrade to the version in sourceDir. The caller passes the
    /// version being installed (normally ProductVersion of the new
    /// build). Returns null on success, or an error after rolling
    /// the shortcut/Run targets back to the previous version.
    /// </summary>
    public static string? Upgrade(string sourceDir, string newVersion, InstallLayout layout, Func<string, bool> waitHealthy)
    {
        var previous = layout.CurrentVersion();
        var (manifest, error) = layout.InstallFrom(sourceDir, newVersion);
        if (error is not null)
            return error;
        _ = manifest;
        var newExe = InstalledExePath(layout, newVersion);
        var oldExe = previous is null ? null : InstalledExePath(layout, previous);
        try
        {
            CreateShortcut(StartMenuShortcut, newExe);
            if (AgentConfig.Load(layout.ConfigFile).Autostart)
                SetAutostart(true, newExe);
        }
        catch (Exception ex)
        {
            return "shortcut_or_startup_failed: " + ex.Message;
        }
        // Launch the new version and wait for its healthy marker.
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = newExe,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            RollbackShortcuts(layout, previous, oldExe);
            layout.RemoveVersion(newVersion);
            return "launch_failed: " + ex.Message;
        }
        if (!waitHealthy(newExe))
        {
            RollbackShortcuts(layout, previous, oldExe);
            layout.RemoveVersion(newVersion);
            if (oldExe is not null)
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = oldExe,
                        UseShellExecute = true,
                    });
                }
                catch { }
            }
            return "new_version_unhealthy";
        }
        // Success: keep the previous version dir as rollback
        // material; drop versions older than the previous one.
        if (previous is not null)
        {
            foreach (var dir in Directory.EnumerateDirectories(layout.AppRoot))
            {
                var name = Path.GetFileName(dir);
                if (name == previous || name == newVersion || name.Contains(".tmp-", StringComparison.Ordinal))
                    continue;
                layout.RemoveVersion(name);
            }
        }
        return null;
    }

    private static void RollbackShortcuts(InstallLayout layout, string? previous, string? oldExe)
    {
        try
        {
            if (previous is not null && oldExe is not null)
            {
                layout.WriteCurrent(previous);
                CreateShortcut(StartMenuShortcut, oldExe);
                if (AgentConfig.Load(layout.ConfigFile).Autostart)
                    SetAutostart(true, oldExe);
            }
        }
        catch { }
    }

    /// <summary>Remove Run key + shortcut, then schedule the install dir for deletion after exit.</summary>
    public static void Uninstall(InstallLayout layout)
    {
        try { SetAutostart(false, string.Empty); } catch { }
        try { if (File.Exists(StartMenuShortcut)) File.Delete(StartMenuShortcut); } catch { }
        var pid = Environment.ProcessId;
        var root = layout.Root;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-WindowStyle Hidden -NoProfile -Command " +
                    $"\"Wait-Process -Id {pid} -ErrorAction SilentlyContinue; " +
                    $"Start-Sleep -Seconds 1; " +
                    $"Remove-Item -LiteralPath '{root.Replace("'", "''")}' -Recurse -Force -ErrorAction SilentlyContinue\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch { }
    }

    public static void SetAutostart(bool enabled, string exePath)
    {
        using var key = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
        if (key is null)
            throw new InvalidOperationException("Cannot open the Run key.");
        if (enabled)
            key.SetValue(RunKeyName, "\"" + exePath + "\"");
        else if (key.GetValue(RunKeyName) is not null)
            key.DeleteValue(RunKeyName);
    }

    // ---- .lnk via IShellLinkW (no COM reference, no dynamic) ----

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszFile, int cchMaxPath, out IntPtr pfd, int fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszName, int cchMaxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszDir, int cchMaxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszArgs, int cchMaxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszIconPath, int cchIconPath, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, int dwReserved);
        void Resolve(IntPtr hwnd, int fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("0000010B-0000-0000-C000-000000000046")]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        void IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, int dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }

    public static void CreateShortcut(string shortcutPath, string targetExe)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(shortcutPath)!);
        var type = Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046"));
        var link = (IShellLinkW)Activator.CreateInstance(type!)!;
        link.SetPath(targetExe);
        link.SetWorkingDirectory(Path.GetDirectoryName(targetExe)!);
        link.SetDescription("Windows Bridge — control this computer from Muse");
        ((IPersistFile)link).Save(shortcutPath, false);
    }
}
