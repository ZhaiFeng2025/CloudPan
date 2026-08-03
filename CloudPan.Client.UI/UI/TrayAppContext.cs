using CloudPan.Client.Core.Models;
using CloudPan.Client.Core.Services;
using CloudPan.Infrastructure.Design;
using Microsoft.Win32;

namespace CloudPan.Client.UI;

/// <summary>
/// 系统托盘应用上下文——管理托盘图标和右键菜单。
/// </summary>
public class TrayAppContext : ApplicationContext
{
    public static NotifyIcon? TrayIcon { get; private set; }

    private readonly NotifyIcon _trayIcon;
    private readonly MainWindow _mainWindow;
    private readonly Icon _normalIcon;
    private readonly Task _syncTask;
    private readonly Task _wsTask;
    private readonly CancellationTokenSource _cts = new();
    private readonly SyncEngine _engine;
    private readonly WebSocketClient _wsClient;
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _conflictPaths = new();
    private readonly System.Threading.SynchronizationContext? _syncCtx; // UI 同步上下文（构造函数捕获，供具名事件处理器）
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _recentActivity = new(); // 最近同步活动（托盘文本）
    private volatile bool _isPaused;

    /// <summary>重配引导已提示过（F-34/T-034）：防 HTTP 队列与 WebSocket 双重 401 同时弹两次；连接恢复/重配成功后重置。</summary>
    private volatile bool _reconfigPromptShown;

    public TrayAppContext(SyncEngine engine, WebSocketClient wsClient)
    {
        _engine = engine;
        _wsClient = wsClient;
        _mainWindow = new MainWindow(engine);

        // ===== 托盘图标 =====
        // 不设 ContextMenuStrip，避免拦截鼠标事件。
        // 左键→窗口 / 右键→动态构建菜单
        _trayIcon = new NotifyIcon
        {
            Icon = IconFactory.CreateClient(),
            Text = "CloudPan — 文件同步",
            Visible = true
        };
        TrayIcon = _trayIcon;
        _normalIcon = IconFactory.CreateClient();

        _trayIcon.MouseUp += TrayIcon_MouseUp;

        // 捕获 UI 线程同步上下文（提升为字段，供具名事件处理器使用）
        _syncCtx = System.Threading.SynchronizationContext.Current;

        // 启动同步引擎
        _syncTask = Task.Run(() => engine.StartAsync(_cts.Token));
        _syncTask.ContinueWith(t =>
        {
            if (t.IsFaulted)
            {
                var ex = t.Exception?.InnerException ?? t.Exception;
                string msg = ex?.Message ?? "未知错误";
                _syncCtx?.Post(_ =>
                {
                    _trayIcon.ShowBalloonTip(10000, "CloudPan — 同步异常",
                        $"同步引擎已停止: {msg}\n请检查网络或重新启动客户端。", ToolTipIcon.Error);
                    _trayIcon.Icon = SystemIcons.Error;
                    _trayIcon.Text = "CloudPan — 同步异常";
                }, null);
            }
        }, TaskContinuationOptions.OnlyOnFaulted);

        // 启动 WebSocket 客户端
        _wsTask = Task.Run(() => wsClient.StartAsync(_cts.Token));
        _wsTask.ContinueWith(t =>
        {
            if (t.IsFaulted)
            {
                var ex = t.Exception?.InnerException ?? t.Exception;
                string msg = ex?.Message ?? "未知错误";
                _syncCtx?.Post(_ =>
                {
                    _trayIcon.ShowBalloonTip(10000, "CloudPan — 连接异常",
                        $"服务端连接已断开: {msg}\n客户端将自动重连。", ToolTipIcon.Warning);
                }, null);
            }
        }, TaskContinuationOptions.OnlyOnFaulted);

        // 冲突检测 → 托盘气泡 + 警告图标
        engine.ConflictDetected += OnConflictDetected;
        // 冲突解决
        engine.ConflictResolved += OnConflictResolved;
        // 断连 / 重连通知
        wsClient.OnDisconnected += OnWsDisconnected;
        wsClient.OnConnected += OnWsConnected;
        // 认证失败
        wsClient.OnPermanentFailure += OnWsPermanentFailure;
        // F-34/T-034：连续 401（Token 或服务端配置已变更）→ 重配引导
        engine.ReconfigurationRequired += OnReconfigurationRequired;
        // 状态更新
        engine.StatusChanged += OnStatusChanged;
    }

    // ===== 具名事件处理器（CP301：避免匿名 lambda 订阅无法退订） =====

    /// <summary>托盘鼠标事件：左键显示窗口，右键显示菜单。</summary>
    private void TrayIcon_MouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            ShowWindow();
        }
        else if (e.Button == MouseButtons.Right)
        {
            ShowTrayMenu();
        }
    }

    /// <summary>
    /// 冲突检测 → 托盘气泡 + 警告图标。
    /// T-036：聚合与非模态列表由 MainWindow.OnConflictDetected（引擎事件直接订阅）负责，
    /// 此处不再调用 ShowConflictWarning，避免同一冲突被二次加入/重复弹窗。
    /// </summary>
    private void OnConflictDetected(ConflictInfo conflictInfo)
    {
        string path = conflictInfo.RelativePath;
        _syncCtx?.Post(_ =>
        {
            _conflictPaths.Enqueue(path);
            if (_conflictPaths.Count > 50) { _conflictPaths.TryDequeue(out string? _); }

            _trayIcon.Icon = SystemIcons.Warning;
            _trayIcon.Text = "CloudPan — 文件冲突";
            _trayIcon.ShowBalloonTip(5000, "CloudPan — 文件冲突",
                $"检测到文件冲突: {path}\n点击查看详情", ToolTipIcon.Warning);
        }, null);
    }

    /// <summary>冲突解决：从队列移除并恢复图标。</summary>
    private void OnConflictResolved(string path)
    {
        _syncCtx?.Post(_ =>
        {
            List<string> remaining = new List<string>();
            while (_conflictPaths.TryDequeue(out string? p))
            {
                if (p != path) remaining.Add(p);
            }
            foreach (string p in remaining) _conflictPaths.Enqueue(p);

            if (_conflictPaths.Count == 0 && !Program.IsOffline)
            {
                _trayIcon.Icon = _normalIcon;
                _trayIcon.Text = "CloudPan — 已连接";
            }
        }, null);
    }

    /// <summary>WebSocket 断开：离线状态。</summary>
    private void OnWsDisconnected()
    {
        _syncCtx?.Post(_ =>
        {
            _trayIcon.Icon = SystemIcons.Warning;
            _trayIcon.Text = "CloudPan — 已离线（自动重连中）";
            _trayIcon.ShowBalloonTip(5000, "CloudPan", "服务端连接已断开，正在自动重连...", ToolTipIcon.Warning);
        }, null);
    }

    /// <summary>WebSocket 重连成功：恢复在线状态。</summary>
    private void OnWsConnected()
    {
        // 连接恢复（Token 已有效）→ 允许未来再次触发重配引导
        _reconfigPromptShown = false;
        _syncCtx?.Post(_ =>
        {
            _trayIcon.Icon = _normalIcon;
            _trayIcon.Text = "CloudPan — 已连接";
            _trayIcon.ShowBalloonTip(3000, "CloudPan", "已重新连接到服务端", ToolTipIcon.Info);
        }, null);
    }

    /// <summary>认证失败（WebSocket 侧持续 401）：统一走重配引导（F-34/T-034）。</summary>
    private void OnWsPermanentFailure()
    {
        _syncCtx?.Post(_ =>
        {
            _trayIcon.Icon = SystemIcons.Error;
            _trayIcon.Text = "CloudPan — 需要重新配置";
            ShowReconfigurationPrompt();
        }, null);
    }

    /// <summary>连续 401（同步引擎 HTTP 路径）→ 重配引导（F-34/T-034）。</summary>
    private void OnReconfigurationRequired()
    {
        _syncCtx?.Post(_ => ShowReconfigurationPrompt(), null);
    }

    /// <summary>重配引导：提示「Token 或服务端配置已变更」，用户确认后打开配置页（SetupForm）。</summary>
    private void ShowReconfigurationPrompt()
    {
        if (_reconfigPromptShown)
        {
            return;
        }
        _reconfigPromptShown = true;

        DialogResult result = MessageBox.Show(
            "云盘服务的连接钥匙（Token）或服务端配置已变更，当前连接已失效。\n\n" +
            "是否立即打开配置页面，重新配置家庭服务器地址与连接钥匙？",
            "CloudPan — 需要重新配置",
            MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (result == DialogResult.Yes && StartupFlow.ShowSetupAndSave())
        {
            _reconfigPromptShown = false;
            MessageBox.Show("配置已更新，客户端将自动重启以应用新配置。",
                "CloudPan", MessageBoxButtons.OK, MessageBoxIcon.Information);
            RestartApplication();
        }
    }

    /// <summary>重启客户端进程（新实例启动后退出当前实例），使重新配置后的连接参数生效。</summary>
    private void RestartApplication()
    {
        try
        {
            System.Diagnostics.Process.Start(Application.ExecutablePath);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"重启客户端失败: {ex.Message}");
            return;
        }
        Exit();
    }

    /// <summary>同步状态更新：更新托盘文本/图标并记录日志。</summary>
    private void OnStatusChanged(string status)
    {
        if (status.Contains("上传") || status.Contains("下载") || status.Contains("同步"))
        {
            _recentActivity.Enqueue(status);
        }
        if (_recentActivity.Count > 20) { _recentActivity.TryDequeue(out _); }

        _syncCtx?.Post(_ =>
        {
            string baseText = $"CloudPan — {status}";
            if (_recentActivity.Count > 0 && !status.Contains("就绪"))
            {
                baseText += $"\n{string.Join("\n", _recentActivity.TakeLast(2))}";
            }
            _trayIcon.Text = baseText;
            _trayIcon.Icon = status switch
            {
                string s when s.Contains("错误") || s.Contains("失败") => SystemIcons.Error,
                string s when s.Contains("冲突") || s.Contains("暂停") => SystemIcons.Warning,
                _ => _normalIcon
            };
        }, null);
        _syncCtx?.Post(_ => _mainWindow.AddLog(status), null);
    }

    /// <summary>暂停/继续同步菜单项。</summary>
    private void PauseItem_Click(object? sender, EventArgs e)
    {
        _isPaused = !_isPaused;
        _engine.SetPaused(_isPaused);
        _trayIcon.ShowBalloonTip(3000, "CloudPan",
            _isPaused ? "同步已暂停" : "同步已恢复",
            _isPaused ? ToolTipIcon.Warning : ToolTipIcon.Info);
    }

    /// <summary>查看冲突菜单项：打开主窗的非模态聚合冲突列表（T-036）。</summary>
    private void ConflictItem_Click(object? sender, EventArgs e)
    {
        ShowWindow();
        _mainWindow.ShowConflictList();
    }

    /// <summary>开机自启菜单项：切换并持久化状态。</summary>
    private void AutoStartItem_Click(object? sender, EventArgs e)
    {
        bool newState = !IsAutoStartEnabled();
        SetAutoStart(newState);
        if (sender is ToolStripMenuItem item)
        {
            item.Checked = newState;
        }
    }

    // ===== 右键菜单（运行时动态构建） =====

    private void ShowTrayMenu()
    {
        var menu = new ContextMenuStrip();

        menu.Items.Add("显示主窗口", null, (_, _) => ShowWindow());
        menu.Items.Add("打开同步文件夹", null, (_, _) => OpenFolder());
        menu.Items.Add("打开日志目录", null, (_, _) => OpenLogDir());
        menu.Items.Add(new ToolStripSeparator());

        // 暂停/继续
        var pauseItem = new ToolStripMenuItem(_isPaused ? "继续同步" : "暂停同步");
        pauseItem.Click += PauseItem_Click;
        menu.Items.Add(pauseItem);

        menu.Items.Add("立即同步", null, async (_, _) =>
        {
            _trayIcon.Icon = _normalIcon;
            _trayIcon.Text = "CloudPan — 正在同步";
            if (_isPaused)
            {
                _isPaused = false;
                _engine.SetPaused(false);
                _trayIcon.ShowBalloonTip(3000, "CloudPan", "同步已恢复，正在重新扫描变更...", ToolTipIcon.Info);
            }
            else
            {
                _trayIcon.ShowBalloonTip(3000, "CloudPan", "正在重新扫描变更...", ToolTipIcon.Info);
            }
            try { await _engine.FullScanAsync(_cts.Token); }
            catch (OperationCanceledException) { }
        });
        menu.Items.Add(new ToolStripSeparator());

        // 查看冲突
        int conflictCount = _conflictPaths.Count;
        var conflictItem = new ToolStripMenuItem(conflictCount > 0 ? $"查看冲突 ({conflictCount})" : "查看冲突")
        {
            Enabled = conflictCount > 0
        };
        conflictItem.Click += ConflictItem_Click;
        menu.Items.Add(conflictItem);
        menu.Items.Add(new ToolStripSeparator());

        // T-018：分享 + 版本历史入口（对文件浏览当前选中文件生效；未选中时提示）
        menu.Items.Add("分享当前文件…", null, (_, _) =>
        {
            ShowWindow();
            _mainWindow.OpenShareForSelection();
        });
        menu.Items.Add("版本历史…", null, (_, _) =>
        {
            ShowWindow();
            _mainWindow.OpenVersionHistoryForSelection();
        });
        menu.Items.Add(new ToolStripSeparator());

        // 开机自启
        var autoStartItem = new ToolStripMenuItem("开机自动启动")
        {
            CheckOnClick = true,
            Checked = IsAutoStartEnabled()
        };
        autoStartItem.Click += AutoStartItem_Click;
        menu.Items.Add(autoStartItem);
        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add("设置", null, (_, _) => OpenSettings());
        menu.Items.Add("关于", null, (_, _) =>
        {
            var ver = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version;
            string verStr = ver != null ? $"{ver.Major}.{ver.Minor}.{ver.Build}" : "1.0.0";
            MessageBox.Show(
                $"CloudPan 文件同步系统\n版本 {verStr}\n\n自托管家庭文件同步\n数据完全保存在您的设备上\n\n同步目录: {Program.SyncRoot}\n服务端: {Program.ServerUrl}",
                "关于 CloudPan", MessageBoxButtons.OK, MessageBoxIcon.Information);
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => Exit());

        menu.Show(Cursor.Position);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cts.Cancel();
            _engine.Stop();        // 停止同步引擎（含 FileWatcher、信号量释放）
            _wsClient.Stop();      // 停止 WebSocket 连接
            _cts.Dispose();
            _trayIcon.Dispose();
            _normalIcon.Dispose();
            _mainWindow.Dispose();
            _engine.ReconfigurationRequired -= OnReconfigurationRequired; // 退订重配引导事件（CP300）
            _engine.Dispose();     // 释放 SyncEngine（取消 WS 事件订阅、释放 _syncLock、_fileWatcher）
            _wsClient.Dispose();   // 释放 WebSocketClient（Socket、信号量、事件委托）
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
        catch (Exception ex) { Console.Error.WriteLine($"打开文件夹失败: {ex.Message}"); }
    }

    private void OpenLogDir()
    {
        string logDir = Path.Combine(Program.SyncRoot, ".cloudpan", "logs");
        try
        {
            if (Directory.Exists(logDir))
            {
                System.Diagnostics.Process.Start("explorer.exe", logDir);
            }
            else
            {
                MessageBox.Show("日志目录尚不存在，将在首次同步后生成。", "CloudPan",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"打开日志目录失败: {ex.Message}"); }
    }

    private static bool IsAutoStartEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", false);
            return key?.GetValue("CloudPan") != null;
        }
        catch { return false; }
    }

    private static void SetAutoStart(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
            if (key == null) return;
            if (enable)
            {
                key.SetValue("CloudPan", $"\"{Application.ExecutablePath}\"");
            }
            else
            {
                key.DeleteValue("CloudPan", false);
            }
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"设置开机自启失败: {ex.Message}"); }
    }

    private void OpenSettings()
    {
        try
        {
            ClientConfig cfg = ClientConfig.Load(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CloudPan", "client-config.json"));
            SettingsForm form = new SettingsForm(
                Program.ServerUrl, Program.SyncRoot, Program.Token,
                cfg.UploadLimitBps, cfg.DownloadLimitBps, cfg.SelectedPaths);
            if (form.ShowDialog() == DialogResult.OK)
            {
                cfg.ServerUrl = form.ServerUrl;
                cfg.SyncRoot = form.SyncRoot;
                cfg.TokenEncrypted = Convert.ToBase64String(
                    System.Security.Cryptography.ProtectedData.Protect(
                        System.Text.Encoding.UTF8.GetBytes(form.Token), null,
                        System.Security.Cryptography.DataProtectionScope.CurrentUser));
                cfg.UploadLimitBps = form.UploadLimitBps;
                cfg.DownloadLimitBps = form.DownloadLimitBps;
                cfg.SelectedPaths = form.SelectedPaths;
                cfg.Save(Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "CloudPan", "client-config.json"));
                MessageBox.Show("设置已保存。部分更改需要重启客户端后生效。",
                    "CloudPan", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"设置保存失败:\n{ex.Message}\n\n请检查磁盘空间和写入权限。",
                "CloudPan", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void Exit()
    {
        Program.IsOffline = true;
        _cts.Cancel();
        _trayIcon.Visible = false;
        Application.Exit();
    }
}
