using Microsoft.Win32;

namespace CloudPan.Client.UI;

/// <summary>
/// 系统托盘应用上下文——管理托盘图标和右键菜单。
/// </summary>
public class TrayAppContext : ApplicationContext
{
    /// <summary>静态引用，供 MainWindow 关闭时弹出托盘气泡。</summary>
    public static NotifyIcon? TrayIcon { get; private set; }

    private readonly NotifyIcon _trayIcon;
    private readonly MainWindow _mainWindow;
    private readonly Icon _normalIcon;
    private readonly Task _syncTask;
    private readonly Task _wsTask;
    private readonly CancellationTokenSource _cts = new();
    private readonly Services.SyncEngine _engine;
    private readonly Services.WebSocketClient _wsClient;
    private readonly ToolStripMenuItem _viewConflictsItem;
    private readonly ToolStripMenuItem _pauseResumeItem;
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _conflictPaths = new();
    private volatile bool _isPaused;

    public TrayAppContext(Services.SyncEngine engine, Services.WebSocketClient wsClient)
    {
        _engine = engine;
        _wsClient = wsClient;
        _mainWindow = new MainWindow(engine);

        // 托盘图标（使用自绘 CloudPan 图标）
        _trayIcon = new NotifyIcon
        {
            Icon = CloudPanIcon.Create(),
            Text = "CloudPan — 文件同步",
            Visible = true,
            ContextMenuStrip = new ContextMenuStrip()
        };
        TrayIcon = _trayIcon; // 供 MainWindow 关闭时弹出气泡
        _normalIcon = CloudPanIcon.Create();

        _trayIcon.ContextMenuStrip.Items.Add("显示主窗口", null, (_, _) => ShowWindow());
        _trayIcon.ContextMenuStrip.Items.Add("打开同步文件夹", null, (_, _) => OpenFolder());
        _trayIcon.ContextMenuStrip.Items.Add("打开日志目录", null, (_, _) => OpenLogDir());
        _trayIcon.ContextMenuStrip.Items.Add(new ToolStripSeparator());
        // 暂停/继续同步切换
        _pauseResumeItem = new ToolStripMenuItem("暂停同步");
        _pauseResumeItem.Click += (_, _) =>
        {
            _isPaused = !_isPaused;
            engine.SetPaused(_isPaused);
            _pauseResumeItem.Text = _isPaused ? "继续同步" : "暂停同步";
            _trayIcon.ShowBalloonTip(3000, "CloudPan",
                _isPaused ? "同步已暂停" : "同步已恢复",
                _isPaused ? ToolTipIcon.Warning : ToolTipIcon.Info);
        };
        _trayIcon.ContextMenuStrip.Items.Add(_pauseResumeItem);
        _trayIcon.ContextMenuStrip.Items.Add("立即同步", null, async (_, _) =>
        {
            // 清除错误状态：重置图标和文字
            _trayIcon.Icon = _normalIcon;
            _trayIcon.Text = "CloudPan — 正在同步";

            if (_isPaused)
            {
                _isPaused = false;
                engine.SetPaused(false);
                _pauseResumeItem.Text = "暂停同步";
                _trayIcon.ShowBalloonTip(3000, "CloudPan", "同步已恢复，正在重新扫描变更...", ToolTipIcon.Info);
            }
            else
            {
                _trayIcon.ShowBalloonTip(3000, "CloudPan", "正在重新扫描变更...", ToolTipIcon.Info);
            }
            try
            {
                // 全量扫描会重新发现本地变更（包括之前因错误放弃的文件），将其重新入队
                await engine.FullScanAsync(_cts.Token);
            }
            catch (OperationCanceledException) { }
        });
        _trayIcon.ContextMenuStrip.Items.Add(new ToolStripSeparator());
        _viewConflictsItem = new ToolStripMenuItem("查看冲突") { Visible = false };
        _viewConflictsItem.Click += (_, _) =>
        {
            _mainWindow.Show();
            _mainWindow.WindowState = FormWindowState.Normal;
            _mainWindow.Activate();
            if (_conflictPaths.TryDequeue(out string? lastPath))
            {
                _mainWindow.ShowConflictWarning(lastPath);
            }
        };
        _trayIcon.ContextMenuStrip.Items.Add(_viewConflictsItem);
        _trayIcon.ContextMenuStrip.Items.Add(new ToolStripSeparator());
        // 开机自启开关
        ToolStripMenuItem autoStartItem = new ToolStripMenuItem("开机自动启动") { CheckOnClick = true };
        autoStartItem.Checked = IsAutoStartEnabled();
        autoStartItem.CheckState = autoStartItem.Checked ? CheckState.Checked : CheckState.Unchecked;
        autoStartItem.Click += (_, _) =>
        {
            bool newState = !IsAutoStartEnabled();
            SetAutoStart(newState);
            autoStartItem.Checked = newState;
            autoStartItem.CheckState = newState ? CheckState.Checked : CheckState.Unchecked;
        };
        _trayIcon.ContextMenuStrip.Items.Add(autoStartItem);
        _trayIcon.ContextMenuStrip.Items.Add(new ToolStripSeparator());
        _trayIcon.ContextMenuStrip.Items.Add("设置", null, (_, _) => OpenSettings());
        _trayIcon.ContextMenuStrip.Items.Add("关于", null, (_, _) =>
        {
            var ver = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version;
            string verStr = ver != null ? $"{ver.Major}.{ver.Minor}.{ver.Build}" : "1.0.0";
            MessageBox.Show(
                $"CloudPan 文件同步系统\n版本 {verStr}\n\n自托管家庭文件同步\n数据完全保存在您的设备上\n\n同步目录: {Program.SyncRoot}\n服务端: {Program.ServerUrl}",
                "关于 CloudPan", MessageBoxButtons.OK, MessageBoxIcon.Information);
        });
        _trayIcon.ContextMenuStrip.Items.Add(new ToolStripSeparator());
        _trayIcon.ContextMenuStrip.Items.Add("退出", null, (_, _) => Exit());

        // 左键单击/双击 → 显示管理窗口
        // 注：当 ContextMenuStrip 不为 null 时，Click/DoubleClick 可能不触发
        _trayIcon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                ShowWindow();
            }
        };

        // 捕获 UI 线程同步上下文，用于后续封送到 UI 线程
        var syncCtx = System.Threading.SynchronizationContext.Current;
        // 启动同步引擎（后台运行），异常时通知用户
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
                    // 将托盘图标切换为错误图标
                    _trayIcon.Icon = SystemIcons.Error;
                    _trayIcon.Text = "CloudPan — 同步异常";
                }, null);
            }
        }, TaskContinuationOptions.OnlyOnFaulted);

        // 启动 WebSocket 客户端（后台运行）
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

        // 冲突检测 → 托盘气泡（含"点击查看详情"） + 警告图标 + 菜单项显示冲突数量

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

                int count = _conflictPaths.Count;
                _viewConflictsItem.Text = $"查看冲突 ({count})";
                _viewConflictsItem.Visible = true;

                _mainWindow.ShowConflictWarning(path);
            }, null);
        };

        // 冲突解决后从队列和菜单中清除
        engine.ConflictResolved += (path) =>
        {
            syncCtx?.Post(_ =>
            {
                // 从 ConcurrentQueue 中移除已解决的冲突
                List<string> remaining = new System.Collections.Generic.List<string>();
                while (_conflictPaths.TryDequeue(out string? p))
                {
                    if (p != path)
                    {
                        remaining.Add(p);
                    }
                }
                foreach (string p in remaining)
                {
                    _conflictPaths.Enqueue(p);
                }

                int count = _conflictPaths.Count;
                _viewConflictsItem.Text = count > 0 ? "查看冲突 (" + count + ")" : "查看冲突";
                _viewConflictsItem.Visible = count > 0;
                if (count == 0 && !Program.IsOffline)
                {
                    _trayIcon.Icon = _normalIcon;
                    _trayIcon.Text = "CloudPan — 已连接";
                }
            }, null);
        };

        // 断连通知 → 托盘气泡 + 离线图标
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

        // 认证永久失败（Token 无效等）——通知用户重新配置
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

        // 状态更新 → 图标颜色 + 日志 + 托盘文字
        System.Collections.Concurrent.ConcurrentQueue<string> recentActivity = new System.Collections.Concurrent.ConcurrentQueue<string>();
        engine.StatusChanged += (status) =>
        {
            if (status.Contains("上传") || status.Contains("下载") || status.Contains("同步"))
            {
                recentActivity.Enqueue(status);
            }

            if (recentActivity.Count > 20)
            {
                recentActivity.TryDequeue(out _);
            }

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
            // 通过 UI 同步上下文封送——AddLog 内部也做 InvokeRequired 检查，双重保险
            syncCtx?.Post(_ => _mainWindow.AddLog(status), null);
        };
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
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"检查开机自启注册表失败: {ex.Message}"); return false; }
    }

    private static void SetAutoStart(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
            if (key == null)
            {
                return;
            }

            if (enable)
            {
                key.SetValue("CloudPan", $"\"{Application.ExecutablePath}\"");
            }
            else
            {
                key.DeleteValue("CloudPan", false);
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"设置开机自启失败: {ex.Message}"); }
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

    private async void Exit()
    {
        _cts.Cancel();
        _engine.Stop();
        _wsClient.Stop();
        _trayIcon.Visible = false;
        try { await Task.WhenAny(_syncTask, Task.Delay(10000)); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"等待同步任务停止超时: {ex.Message}"); }
        try { await Task.WhenAny(_wsTask, Task.Delay(5000)); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"等待 WebSocket 任务停止超时: {ex.Message}"); }
        // 释放资源（WebSocketClient CTS、FileWatcherService Timer 等）
        _wsClient.Dispose();
        _engine.Dispose();
        Application.Exit();
    }
}
