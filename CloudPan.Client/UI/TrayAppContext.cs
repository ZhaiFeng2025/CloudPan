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
    private readonly Services.SyncEngine _engine;
    private readonly Services.WebSocketClient _wsClient;
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _conflictPaths = new();
    private volatile bool _isPaused;

    public TrayAppContext(Services.SyncEngine engine, Services.WebSocketClient wsClient)
    {
        _engine = engine;
        _wsClient = wsClient;
        _mainWindow = new MainWindow(engine);

        // ===== 托盘图标 =====
        // 不设 ContextMenuStrip，避免拦截鼠标事件。
        // 左键→窗口 / 右键→动态构建菜单
        _trayIcon = new NotifyIcon
        {
            Icon = CloudPanIcon.Create(),
            Text = "CloudPan — 文件同步",
            Visible = true
        };
        TrayIcon = _trayIcon;
        _normalIcon = CloudPanIcon.Create();

        _trayIcon.MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                ShowWindow();
            }
            else if (e.Button == MouseButtons.Right)
            {
                ShowTrayMenu();
            }
        };

        // 捕获 UI 线程同步上下文
        var syncCtx = System.Threading.SynchronizationContext.Current;

        // 启动同步引擎
        _syncTask = Task.Run(() => engine.StartAsync(_cts.Token));
        _syncTask.ContinueWith(t =>
        {
            if (t.IsFaulted)
            {
                var ex = t.Exception?.InnerException ?? t.Exception;
                string msg = ex?.Message ?? "未知错误";
                syncCtx?.Post(_ =>
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
                syncCtx?.Post(_ =>
                {
                    _trayIcon.ShowBalloonTip(10000, "CloudPan — 连接异常",
                        $"服务端连接已断开: {msg}\n客户端将自动重连。", ToolTipIcon.Warning);
                }, null);
            }
        }, TaskContinuationOptions.OnlyOnFaulted);

        // 冲突检测 → 托盘气泡 + 警告图标
        engine.ConflictDetected += (conflictInfo) =>
        {
            string path = conflictInfo.RelativePath;
            syncCtx?.Post(_ =>
            {
                _conflictPaths.Enqueue(path);
                if (_conflictPaths.Count > 50) { _conflictPaths.TryDequeue(out string? _); }

                _trayIcon.Icon = SystemIcons.Warning;
                _trayIcon.Text = "CloudPan — 文件冲突";
                _trayIcon.ShowBalloonTip(5000, "CloudPan — 文件冲突",
                    $"检测到文件冲突: {path}\n点击查看详情", ToolTipIcon.Warning);

                _mainWindow.ShowConflictWarning(path);
            }, null);
        };

        // 冲突解决
        engine.ConflictResolved += (path) =>
        {
            syncCtx?.Post(_ =>
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
        };

        // 断连通知
        wsClient.OnDisconnected += () =>
        {
            syncCtx?.Post(_ =>
            {
                _trayIcon.Icon = SystemIcons.Warning;
                _trayIcon.Text = "CloudPan — 已离线（自动重连中）";
                _trayIcon.ShowBalloonTip(5000, "CloudPan", "服务端连接已断开，正在自动重连...", ToolTipIcon.Warning);
            }, null);
        };
        wsClient.OnConnected += () =>
        {
            syncCtx?.Post(_ =>
            {
                _trayIcon.Icon = _normalIcon;
                _trayIcon.Text = "CloudPan — 已连接";
                _trayIcon.ShowBalloonTip(3000, "CloudPan", "已重新连接到服务端", ToolTipIcon.Info);
            }, null);
        };

        // 认证失败
        wsClient.OnPermanentFailure += () =>
        {
            syncCtx?.Post(_ =>
            {
                _trayIcon.Icon = SystemIcons.Error;
                _trayIcon.Text = "CloudPan — Token 无效，请重新配置";
                _trayIcon.ShowBalloonTip(5000, "CloudPan",
                    "Token 认证失败已达上限。请检查 Token 是否正确或服务端是否已重新生成。",
                    ToolTipIcon.Error);
            }, null);
        };

        // 状态更新
        System.Collections.Concurrent.ConcurrentQueue<string> recentActivity = new();
        engine.StatusChanged += (status) =>
        {
            if (status.Contains("上传") || status.Contains("下载") || status.Contains("同步"))
            {
                recentActivity.Enqueue(status);
            }
            if (recentActivity.Count > 20) { recentActivity.TryDequeue(out _); }

            syncCtx?.Post(_ =>
            {
                string baseText = $"CloudPan — {status}";
                if (recentActivity.Count > 0 && !status.Contains("就绪"))
                {
                    baseText += $"\n{string.Join("\n", recentActivity.TakeLast(2))}";
                }
                _trayIcon.Text = baseText;
                _trayIcon.Icon = status switch
                {
                    string s when s.Contains("错误") || s.Contains("失败") => SystemIcons.Error,
                    string s when s.Contains("冲突") || s.Contains("暂停") => SystemIcons.Warning,
                    _ => _normalIcon
                };
            }, null);
            syncCtx?.Post(_ => _mainWindow.AddLog(status), null);
        };
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
        pauseItem.Click += (_, _) =>
        {
            _isPaused = !_isPaused;
            _engine.SetPaused(_isPaused);
            _trayIcon.ShowBalloonTip(3000, "CloudPan",
                _isPaused ? "同步已暂停" : "同步已恢复",
                _isPaused ? ToolTipIcon.Warning : ToolTipIcon.Info);
        };
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
        conflictItem.Click += (_, _) =>
        {
            ShowWindow();
            if (_conflictPaths.TryDequeue(out string? lastPath))
            {
                _mainWindow.ShowConflictWarning(lastPath);
            }
        };
        menu.Items.Add(conflictItem);
        menu.Items.Add(new ToolStripSeparator());

        // 开机自启
        var autoStartItem = new ToolStripMenuItem("开机自动启动")
        {
            CheckOnClick = true,
            Checked = IsAutoStartEnabled()
        };
        autoStartItem.Click += (_, _) =>
        {
            bool newState = !IsAutoStartEnabled();
            SetAutoStart(newState);
            autoStartItem.Checked = newState;
        };
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
            _cts.Dispose();
            _trayIcon.Dispose();
            _normalIcon.Dispose();
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
            Models.ClientConfig cfg = Models.ClientConfig.Load(
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
