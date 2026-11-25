using System.Text.Json;
using Bridge.DesktopCore;
using Bridge.WindowsAgent.Backend;
using Bridge.WindowsAgent.Install;

namespace Bridge.WindowsAgent;

internal static class Program
{
    /// <summary>
    /// The command router the device channel loop will call once
    /// the transport exists (T20/T23). Until then it is the seam
    /// proving the file backend is reachable through the same
    /// envelope path as desktop input.
    /// </summary>
    internal static AgentCommandRouter? Router { get; private set; }

    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        var installRoot = Installer.InstallRoot;
        var exePath = Environment.ProcessPath ?? Application.ExecutablePath;
        var layout = new InstallLayout(installRoot);

        // First run from outside the install directory: installer mode.
        if (!Installer.IsInstalledRun(exePath, installRoot))
        {
            var sourceDir = AppContext.BaseDirectory;
            Application.Run(new SetupForm(installRoot, sourceDir));
            return;
        }

        // Installed mode: tray agent.
        var config = AgentConfig.Load(layout.ConfigFile);
        WriteHealthyMarker(layout, config.Version);
        var control = new LocalControlState();
        var worker = new DesktopWorkerHost();
        var backend = new WindowsDesktopBackend(worker, control, layout.DataDir);
        var files = new FileBackend(
            config.FileRoot is null ? [] : [config.FileRoot]);
        Router = new AgentCommandRouter(backend, files);
        Application.Run(new BridgeTrayContext(control, backend, files, config, layout));
        backend.DisposeAsync().AsTask().GetAwaiter().GetResult();
        worker.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private static void WriteHealthyMarker(InstallLayout layout, string version)
    {
        try
        {
            Directory.CreateDirectory(layout.DataDir);
            File.WriteAllText(Path.Combine(layout.DataDir, "healthy.json"),
                JsonSerializer.Serialize(new
                {
                    version,
                    started_at = DateTimeOffset.UtcNow,
                    pid = Environment.ProcessId,
                }));
        }
        catch
        {
            // A missing healthy marker only affects upgrade checks.
        }
    }
}
