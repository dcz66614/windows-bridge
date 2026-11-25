using Bridge.DesktopCore;

namespace Bridge.WindowsAgent.Install;

/// <summary>
/// First-run setup dialog (T25). Shown when the agent exe is run
/// from outside the install directory (e.g. from Downloads after
/// extracting the distribution ZIP). Explains what gets installed,
/// where, and asks two explicit choices: autostart and the file
/// access root (default: none — file access stays closed).
/// </summary>
internal sealed class SetupForm : Form
{
    private readonly string _installRoot;
    private readonly string _sourceDir;
    private readonly CheckBox _autostart;
    private readonly TextBox _fileRootBox;
    private readonly Button _installButton;
    private readonly Label _progress;
    private string? _fileRoot;

    public SetupForm(string installRoot, string sourceDir)
    {
        _installRoot = installRoot;
        _sourceDir = sourceDir;
        Text = "安装 Windows Bridge";
        Width = 520;
        Height = 400;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;

        var intro = new Label
        {
            Text = "Windows Bridge 让你用 Muse 控制这台 Windows 电脑。\n\n" +
                   $"将安装到（当前用户，无需管理员）：\n{_installRoot}\n\n" +
                   "安装内容：Bridge 托盘程序 + 桌面工作进程 + 运行库。\n" +
                   "安装后默认处于“尚未绑定”状态，不会自动接受任何控制；\n" +
                   "绑定需要你本人在浏览器里登录并确认。",
            AutoSize = true,
            Location = new Point(16, 16),
        };
        _autostart = new CheckBox
        {
            Text = "开机自动启动 Bridge（登录启动项，可随时在托盘里改）",
            Checked = true,
            AutoSize = true,
            Location = new Point(16, 150),
        };
        var fileLabel = new Label
        {
            Text = "文件访问目录（Muse 只能读写这个目录；默认不授权）：",
            AutoSize = true,
            Location = new Point(16, 185),
        };
        _fileRootBox = new TextBox
        {
            Text = "未授权（文件访问关闭）",
            ReadOnly = true,
            Width = 330,
            Location = new Point(16, 210),
        };
        var pickButton = new Button { Text = "选择…", Location = new Point(356, 208), Width = 70 };
        pickButton.Click += (_, _) => PickFileRoot();
        var clearButton = new Button { Text = "不授权", Location = new Point(432, 208), Width = 70 };
        clearButton.Click += (_, _) =>
        {
            _fileRoot = null;
            _fileRootBox.Text = "未授权（文件访问关闭）";
        };
        _progress = new Label { Text = "", AutoSize = true, Location = new Point(16, 250) };
        _installButton = new Button { Text = "安装", Location = new Point(16, 290), Width = 100 };
        _installButton.Click += async (_, _) => await RunInstallAsync();
        var cancelButton = new Button { Text = "取消", Location = new Point(126, 290), Width = 100 };
        cancelButton.Click += (_, _) => Close();

        Controls.AddRange([intro, _autostart, fileLabel, _fileRootBox, pickButton, clearButton,
            _progress, _installButton, cancelButton]);
    }

    private void PickFileRoot()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "选择 Muse 可以读写的文件目录（只能选一个）",
            UseDescriptionForTitle = true,
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _fileRoot = dialog.SelectedPath;
            _fileRootBox.Text = _fileRoot;
        }
    }

    private async Task RunInstallAsync()
    {
        _installButton.Enabled = false;
        _progress.Text = "正在安装…";
        var layout = new InstallLayout(_installRoot);
        var autostart = _autostart.Checked;
        var fileRoot = _fileRoot;
        var error = await Task.Run(() =>
            Installer.Install(_sourceDir, layout, autostart, fileRoot));
        if (error is not null)
        {
            _progress.Text = "安装失败：" + error;
            _installButton.Enabled = true;
            return;
        }
        _progress.Text = "安装完成，正在启动…";
        var exe = Installer.InstalledExePath(layout, Installer.ProductVersion);
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            _progress.Text = "安装完成，但启动失败：" + ex.Message;
            _installButton.Enabled = true;
            return;
        }
        Close();
    }
}
