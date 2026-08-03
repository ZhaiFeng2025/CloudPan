using CloudPan.Client.Core.Services;

namespace CloudPan.Client.UI;

/// <summary>TrayAppContext 部分类：托盘/引擎/WebSocket 事件处理器与重配引导（具名方法，CP301）。</summary>
public partial class TrayAppContext
{
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
}
