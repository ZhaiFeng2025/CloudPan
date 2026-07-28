namespace CloudPan.Client.UI;

/// <summary>
/// 系统托盘应用上下文——管理托盘图标和右键菜单。
/// </summary>
public class TrayAppContext : ApplicationContext
{
    private readonly NotifyIcon _trayIcon;
    private readonly MainWindow _mainWindow;
    private readonly Task _syncTask;
    private readonly CancellationTokenSource _cts = new();

    public TrayAppContext(Services.SyncEngine engine)
    {
        _mainWindow = new MainWindow(engine);

        // 托盘图标
        _trayIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "CloudPan — 文件同步",
            Visible = true,
            ContextMenuStrip = new ContextMenuStrip()
        };

        _trayIcon.ContextMenuStrip.Items.Add("显示主窗口", null, (_, _) => ShowWindow());
        _trayIcon.ContextMenuStrip.Items.Add("打开同步文件夹", null, (_, _) => OpenFolder());
        _trayIcon.ContextMenuStrip.Items.Add(new ToolStripSeparator());
        _trayIcon.ContextMenuStrip.Items.Add("退出", null, (_, _) => Exit());

        _trayIcon.DoubleClick += (_, _) => ShowWindow();

        // 启动同步引擎（后台运行），观察异常防止进程崩溃
        _syncTask = Task.Run(() => engine.StartAsync(_cts.Token));
        _syncTask.ContinueWith(t =>
        {
            if (t.IsFaulted)
                Console.Error.WriteLine($"同步引擎异常终止: {t.Exception}");
        }, TaskContinuationOptions.OnlyOnFaulted);

        // 状态更新 → 托盘提示
        engine.StatusChanged += (status) =>
        {
            _trayIcon.Text = $"CloudPan — {status}";
            _mainWindow.AddLog(status);
        };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cts.Cancel();
            _trayIcon.Dispose();
            _mainWindow.Dispose();
        }
        base.Dispose(disposing);
    }

    private void ShowWindow()
    {
        _mainWindow.Show();
        _mainWindow.WindowState = FormWindowState.Normal;
        _mainWindow.Activate();
    }

    private void OpenFolder()
    {
        try { System.Diagnostics.Process.Start("explorer.exe", Program.SyncRoot); }
        catch { }
    }

    private void Exit()
    {
        _cts.Cancel();
        _trayIcon.Visible = false;
        try { _syncTask.Wait(TimeSpan.FromSeconds(3)); } catch { }
        Application.Exit();
    }
}
