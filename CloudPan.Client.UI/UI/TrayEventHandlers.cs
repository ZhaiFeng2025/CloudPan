using CloudPan.Client.Core.Models;
using CloudPan.Client.Core.Services;

namespace CloudPan.Client.UI;

/// <summary>托盘/引擎/WebSocket 事件处理协作类（T-109）：冲突/断连/重连/重配引导/状态更新（具名方法，CP301）。</summary>
internal sealed class TrayEventHandlers
{
    private readonly TrayAppContext _ctx;

    public TrayEventHandlers(TrayAppContext ctx)
    {
        _ctx = ctx;
    }

    // ===== 具名事件处理器（CP301：避免匿名 lambda 订阅无法退订） =====

    /// <summary>托盘鼠标事件：左键显示窗口，右键显示菜单。</summary>
    public void TrayIcon_MouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            _ctx.ShowWindow();
        }
        else if (e.Button == MouseButtons.Right)
        {
            _ctx._menu.ShowTrayMenu();
        }
    }

    /// <summary>
    /// 冲突检测 → 托盘气泡 + 警告图标。
    /// T-036：聚合与非模态列表由 MainWindow.OnConflictDetected（引擎事件直接订阅）负责，
    /// 此处不再调用 ShowConflictWarning，避免同一冲突被二次加入/重复弹窗。
    /// </summary>
    public void OnConflictDetected(ConflictInfo conflictInfo)
    {
        string path = conflictInfo.RelativePath;
        _ctx._syncCtx?.Post(_ =>
        {
            _ctx._conflictPaths.Enqueue(path);
            if (_ctx._conflictPaths.Count > 50) { _ctx._conflictPaths.TryDequeue(out string? _); }

            _ctx._trayIcon.Icon = SystemIcons.Warning;
            _ctx._trayIcon.Text = "CloudPan — 文件冲突";
            _ctx._trayIcon.ShowBalloonTip(5000, "CloudPan — 文件冲突",
                $"检测到文件冲突: {path}\n点击查看详情", ToolTipIcon.Warning);
        }, null);
    }

    /// <summary>冲突解决：从队列移除并恢复图标。</summary>
    public void OnConflictResolved(string path)
    {
        _ctx._syncCtx?.Post(_ =>
        {
            List<string> remaining = new List<string>();
            while (_ctx._conflictPaths.TryDequeue(out string? p))
            {
                if (p != path) remaining.Add(p);
            }
            foreach (string p in remaining) _ctx._conflictPaths.Enqueue(p);

            if (_ctx._conflictPaths.Count == 0 && !Program.IsOffline)
            {
                _ctx._trayIcon.Icon = _ctx._normalIcon;
                _ctx._trayIcon.Text = "CloudPan — 已连接";
            }
        }, null);
    }

    /// <summary>WebSocket 断开：离线状态。</summary>
    public void OnWsDisconnected()
    {
        _ctx._syncCtx?.Post(_ =>
        {
            _ctx._trayIcon.Icon = SystemIcons.Warning;
            _ctx._trayIcon.Text = "CloudPan — 已离线（自动重连中）";
            _ctx._trayIcon.ShowBalloonTip(5000, "CloudPan", "服务端连接已断开，正在自动重连...", ToolTipIcon.Warning);
        }, null);
    }

    /// <summary>WebSocket 重连成功：恢复在线状态。</summary>
    public void OnWsConnected()
    {
        // 连接恢复（Token 已有效）→ 允许未来再次触发重配引导
        _ctx._reconfigPromptShown = false;
        _ctx._syncCtx?.Post(_ =>
        {
            _ctx._trayIcon.Icon = _ctx._normalIcon;
            _ctx._trayIcon.Text = "CloudPan — 已连接";
            _ctx._trayIcon.ShowBalloonTip(3000, "CloudPan", "已重新连接到服务端", ToolTipIcon.Info);
        }, null);
    }

    /// <summary>认证失败（WebSocket 侧持续 401）：统一走重配引导（F-34/T-034）。</summary>
    public void OnWsPermanentFailure()
    {
        _ctx._syncCtx?.Post(_ =>
        {
            _ctx._trayIcon.Icon = SystemIcons.Error;
            _ctx._trayIcon.Text = "CloudPan — 需要重新配置";
            ShowReconfigurationPrompt();
        }, null);
    }

    /// <summary>连续 401（同步引擎 HTTP 路径）→ 重配引导（F-34/T-034）。</summary>
    public void OnReconfigurationRequired()
    {
        _ctx._syncCtx?.Post(_ => ShowReconfigurationPrompt(), null);
    }

    /// <summary>重配引导：提示「Token 或服务端配置已变更」，用户确认后打开配置页（SetupForm）。</summary>
    private void ShowReconfigurationPrompt()
    {
        if (_ctx._reconfigPromptShown)
        {
            return;
        }
        _ctx._reconfigPromptShown = true;

        DialogResult result = MessageBox.Show(
            "云盘服务的连接钥匙（Token）或服务端配置已变更，当前连接已失效。\n\n" +
            "是否立即打开配置页面，重新配置家庭服务器地址与连接钥匙？",
            "CloudPan — 需要重新配置",
            MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (result == DialogResult.Yes && StartupFlow.ShowSetupAndSave())
        {
            _ctx._reconfigPromptShown = false;
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
        _ctx._actions.Exit();
    }

    /// <summary>同步状态更新：更新托盘文本/图标并记录日志。</summary>
    public void OnStatusChanged(string status)
    {
        if (status.Contains("上传") || status.Contains("下载") || status.Contains("同步"))
        {
            _ctx._recentActivity.Enqueue(status);
        }
        if (_ctx._recentActivity.Count > 20) { _ctx._recentActivity.TryDequeue(out _); }

        _ctx._syncCtx?.Post(_ =>
        {
            string baseText = $"CloudPan — {status}";
            if (_ctx._recentActivity.Count > 0 && !status.Contains("就绪"))
            {
                baseText += $"\n{string.Join("\n", _ctx._recentActivity.TakeLast(2))}";
            }
            _ctx._trayIcon.Text = baseText;
            _ctx._trayIcon.Icon = status switch
            {
                string s when s.Contains("错误") || s.Contains("失败") => SystemIcons.Error,
                string s when s.Contains("冲突") || s.Contains("暂停") => SystemIcons.Warning,
                _ => _ctx._normalIcon
            };
        }, null);
        _ctx._syncCtx?.Post(_ => _ctx._mainWindow.AddLog(status), null);
    }

    /// <summary>暂停/继续同步菜单项。</summary>
    public void PauseItem_Click(object? sender, EventArgs e)
    {
        _ctx._isPaused = !_ctx._isPaused;
        _ctx._engine.SetPaused(_ctx._isPaused);
        _ctx._trayIcon.ShowBalloonTip(3000, "CloudPan",
            _ctx._isPaused ? "同步已暂停" : "同步已恢复",
            _ctx._isPaused ? ToolTipIcon.Warning : ToolTipIcon.Info);
    }

    /// <summary>查看冲突菜单项：打开主窗的非模态聚合冲突列表（T-036）。</summary>
    public void ConflictItem_Click(object? sender, EventArgs e)
    {
        _ctx.ShowWindow();
        _ctx._mainWindow.ShowConflictList();
    }

    /// <summary>开机自启菜单项：切换并持久化状态。</summary>
    public void AutoStartItem_Click(object? sender, EventArgs e)
    {
        bool newState = !TrayActions.IsAutoStartEnabled();
        TrayActions.SetAutoStart(newState);
        if (sender is ToolStripMenuItem item)
        {
            item.Checked = newState;
        }
    }
}
