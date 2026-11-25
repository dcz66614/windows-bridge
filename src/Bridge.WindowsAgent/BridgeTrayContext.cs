using System.Drawing;
using Bridge.DesktopCore;
using Bridge.WindowsAgent.Backend;
using Bridge.WindowsAgent.Install;

namespace Bridge.WindowsAgent;

/// <summary>
/// Local UI + local control state (T19). The pause button latches
/// the pause in LocalControlState (which bumps the epoch and stops
/// the backend), and the tray releases any keys the backend held.
/// Remote calls can never resume: only this local toggle clears it.
/// T25 adds: file-access authorization (local user only) and
/// uninstall. The tray never shows secrets or relay credentials.
/// </summary>
internal sealed class BridgeTrayContext : ApplicationContext
{
    private readonly NotifyIcon _tray;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _status;
    private readonly ToolStripMenuItem _pause;
    private readonly LocalControlState _control;
    private readonly IDesktopBackend _backend;
    private readonly FileBackend _files;
    private AgentConfig _config;
    private readonly InstallLayout _layout;

    public BridgeTrayContext(
        LocalControlState control, IDesktopBackend backend,
        FileBackend files, AgentConfig config, InstallLayout layout)
    {
        _control = control;
        _backend = backend;
        _files = files;
        _config = config;
        _layout = layout;
        _status = new ToolStripMenuItem("尚未绑定 · 无法控制电脑") { Enabled = false };
        _pause = new ToolStripMenuItem("暂停");
        _pause.Click += (_, _) => TogglePause();
        var about = new ToolStripMenuItem("查看状态");
        about.Click += (_, _) => ShowStatus();
        var fileAccess = new ToolStripMenuItem("文件访问…");
        fileAccess.Click += (_, _) => ShowFileAccess();
        var autostart = new ToolStripMenuItem("开机自动启动")
        {
            Checked = _config.Autostart,
            CheckOnClick = true,
        };
        autostart.Click += (_, _) => ToggleAutostart(autostart);
        var uninstall = new ToolStripMenuItem("卸载 Bridge…");
        uninstall.Click += (_, _) => Uninstall();
        var exit = new ToolStripMenuItem("退出");
        exit.Click += (_, _) => ExitThread();
        _menu = new ContextMenuStrip();
        _menu.Items.AddRange([_status, _pause, about, fileAccess, autostart,
            new ToolStripSeparator(), uninstall, exit]);
        _tray = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "Windows Bridge · 尚未绑定",
            ContextMenuStrip = _menu,
            Visible = true
        };
        _tray.DoubleClick += (_, _) => ShowStatus();
    }

    private void TogglePause()
    {
        if (_control.Paused)
        {
            _control.ResumeLocally();
            _pause.Text = "暂停";
            _status.Text = "尚未绑定 · 无法控制电脑";
            _tray.Text = "Windows Bridge · 尚未绑定";
        }
        else
        {
            _control.PauseLocally();
            // Release anything the backend was holding, right now.
            _ = _backend.ReleaseOwnedInputsAsync(CancellationToken.None);
            _pause.Text = "恢复";
            _status.Text = "已暂停 · 尚未绑定";
            _tray.Text = "Windows Bridge · 已暂停";
        }
    }

    private void ShowStatus() => MessageBox.Show(
        $"Windows Bridge {_config.Version}\n\n" +
        "此版本尚未接入 Muse，也未启用屏幕读取或鼠标键盘控制。\n\n" +
        (_control.Paused ? "当前已暂停。\n" : "当前未连接任何控制端。\n") +
        $"文件访问：{(_config.FileRoot ?? "未授权（已关闭）")}\n" +
        $"开机启动：{(_config.Autostart ? "已开启" : "已关闭")}",
        "Windows Bridge 状态", MessageBoxButtons.OK, MessageBoxIcon.Information);

    private void ShowFileAccess()
    {
        var text = _config.FileRoot is null
            ? "当前未授权任何文件目录。Muse 无法读写本机文件。\n\n选择一个目录后，Muse 只能读写该目录内的文件（不能越过目录边界，不能读 UNC、设备路径或 ADS 流）。"
            : $"当前授权目录：\n{_config.FileRoot}\n\nMuse 只能读写该目录内的文件。";
        var result = MessageBox.Show(
            text + "\n\n要更改授权目录吗？（“是”打开文件夹选择，“否”清除授权）",
            "文件访问", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        if (result == DialogResult.Cancel)
            return;
        if (result == DialogResult.No)
        {
            SaveFileRoot(null);
            return;
        }
        using var dialog = new FolderBrowserDialog
        {
            Description = "选择 Muse 可以读写的文件目录（只能选一个）",
            UseDescriptionForTitle = true,
        };
        if (dialog.ShowDialog() == DialogResult.OK)
            SaveFileRoot(dialog.SelectedPath);
    }

    private void SaveFileRoot(string? root)
    {
        _config = _config with { FileRoot = root };
        _config.Save(_layout.ConfigFile);
        _files.SetRoots(root is null ? [] : [root]);
    }

    private void ToggleAutostart(ToolStripMenuItem item)
    {
        try
        {
            Installer.SetAutostart(item.Checked,
                Installer.InstalledExePath(_layout, _config.Version));
            _config = _config with { Autostart = item.Checked };
            _config.Save(_layout.ConfigFile);
        }
        catch (Exception ex)
        {
            item.Checked = !item.Checked;
            MessageBox.Show("修改开机启动失败：" + ex.Message,
                "Windows Bridge", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void Uninstall()
    {
        var result = MessageBox.Show(
            "确定要卸载 Windows Bridge 吗？\n\n将移除开机启动项、开始菜单快捷方式和安装目录，并解除设备绑定（绑定信息在服务端撤销后才完全失效）。",
            "卸载 Windows Bridge", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (result != DialogResult.Yes)
            return;
        Installer.Uninstall(_layout);
        ExitThread();
    }

    protected override void ExitThreadCore()
    {
        _ = _backend.ReleaseOwnedInputsAsync(CancellationToken.None);
        _tray.Visible = false;
        _tray.Dispose();
        _menu.Dispose();
        base.ExitThreadCore();
    }
}
