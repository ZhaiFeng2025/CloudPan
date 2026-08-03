using CloudPan.Client.Core.Composition;

namespace CloudPan.Client.UI;

/// <summary>
/// 启动流程的 UI 交互辅助：配置窗口/进度提示/全局异常处理器/提示对话框。
/// 与 Client.Core 的 ClientBootstrap（启动编排）配对，使 Program.cs 保持薄入口（T-029）。
/// </summary>
internal static class StartupFlow
{
    /// <summary>UI 线程未处理异常：记录崩溃日志并退出。</summary>
    public static void OnThreadException(object sender, System.Threading.ThreadExceptionEventArgs e)
    {
        AppendCrashLog("UI线程异常", e.Exception);
        MessageBox.Show($"CloudPan 遇到未处理的错误，即将退出。\n\n{e.Exception.Message}",
            "CloudPan — 错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        Environment.Exit(1);
    }

    /// <summary>AppDomain 未处理异常：记录崩溃日志。</summary>
    public static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        => AppendCrashLog("未处理异常", e.ExceptionObject);

    /// <summary>未观察 Task 异常：记录并标记已观察，防止进程崩溃。</summary>
    public static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        AppendCrashLog("未观察Task异常", e.Exception);
        e.SetObserved(); // 防止进程崩溃
    }

    /// <summary>Token DPAPI 解密失败提示（Serilog 尚未初始化，用 MessageBox 通知）。</summary>
    public static void NotifyTokenDecryptFailed(string reason) =>
        MessageBox.Show($"Token 解密失败（DPAPI），需重新配置连接。\n\n原因: {reason}",
            "CloudPan — Token 解密失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);

    /// <summary>启动装配失败提示并退出。</summary>
    public static void NotifyStartupFailed(string message) =>
        MessageBox.Show(message, "CloudPan — 启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error);

    /// <summary>数据库损坏：询问是否重建。</summary>
    public static bool ConfirmDatabaseRebuild() =>
        MessageBox.Show("同步数据库已损坏，是否重建？\n\n重建将清空传输队列和同步状态，不影响已同步的文件。",
            "CloudPan — 数据库损坏", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes;

    /// <summary>连接失败：询问是否重新配置连接地址。</summary>
    public static bool AskReconfigure(string serverUrl) =>
        MessageBox.Show($"无法连接到服务端:\n{serverUrl}\n\n" +
            "是否重新配置连接地址？\n\n选择「是」重新配置，选择「否」以离线模式运行。",
            "CloudPan — 连接失败", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes;

    /// <summary>弹出配置窗口并保存配置（DPAPI 加密，保存失败允许重试），返回是否已确认并保存。</summary>
    public static bool ShowSetupAndSave()
    {
        SetupForm setupForm = new SetupForm(Program.ServerUrl, Program.SyncRoot, Program.Token);
        if (setupForm.ShowDialog() != DialogResult.OK)
        {
            return false;
        }

        Program.ServerUrl = setupForm.ServerUrl;
        Program.SyncRoot = setupForm.SyncRoot;
        Program.Token = setupForm.Token;

        while (true)
        {
            try
            {
                ClientBootstrap.SaveConfig(Program.ServerUrl, Program.SyncRoot, Program.Token);
                return true;
            }
            catch (Exception ex)
            {
                var retry = MessageBox.Show($"配置保存失败:\n{ex.Message}\n\n请检查磁盘空间和配置文件路径的写入权限。",
                    "CloudPan — 配置保存失败", MessageBoxButtons.RetryCancel, MessageBoxIcon.Error);
                if (retry != DialogResult.Retry)
                {
                    return false;
                }
            }
        }
    }

    /// <summary>初始化进度提示窗体（防止配置完成后到托盘出现之前的黑屏/闪烁）。</summary>
    public static Form CreateInitForm()
    {
        Form form = new Form
        {
            Text = "CloudPan",
            Size = new Size(320, 90),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            ControlBox = false,
            StartPosition = FormStartPosition.CenterScreen,
            ShowInTaskbar = false,
            MinimizeBox = false,
            MaximizeBox = false,
        };
        form.Controls.Add(new Label
        {
            Text = "正在初始化...",
            Dock = DockStyle.Top,
            TextAlign = ContentAlignment.MiddleCenter,
            Height = 30,
            Top = 15,
        });
        form.Controls.Add(new ProgressBar
        {
            Style = ProgressBarStyle.Marquee,
            Dock = DockStyle.Top,
            Height = 20,
            Top = 45,
        });
        return form;
    }

    /// <summary>崩溃日志路径（%LocalAppData%\CloudPan\crash.log），用于全局异常处理器记录。</summary>
    private static string GetCrashLogPath()
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CloudPan");
        try { Directory.CreateDirectory(dir); } catch { }
        return Path.Combine(dir, "crash.log");
    }

    private static void AppendCrashLog(string kind, object detail)
    {
        try { File.AppendAllText(GetCrashLogPath(), $"[{kind}] {DateTime.UtcNow:O}\n{detail}\n\n"); }
        catch { /* 最后一道防线——写文件也失败则放弃 */ }
    }
}
