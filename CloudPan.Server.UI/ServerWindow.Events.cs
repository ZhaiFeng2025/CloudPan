namespace CloudPan.Server.UI;

/// <summary>ServerWindow 部分类：窗口生命周期事件与空状态/清空日志等具名处理器（CP301；逻辑经 ServerDeviceListView/ServerLogSink 外提）。</summary>
public partial class ServerWindow
{
    // ===== 具名事件处理器（CP301：避免匿名 lambda 订阅无法退订） =====

    private void EmptyStatePanel_Resize(object? sender, EventArgs e) => _devices.CenterEmptyState();

    private void ClearLogBtn_Click(object? sender, EventArgs e)
    {
        _logList.Items.Clear();
        AddLog("日志已清空");
    }

    private async void RefreshTimer_Tick(object? sender, EventArgs e) => await _devices.RefreshAsync();

    /// <summary>关闭按钮 → 隐藏到托盘（而非销毁窗口）。系统/任务管理器关闭时放行。</summary>
    private void Window_FormClosing(object? sender, FormClosingEventArgs e)
    {
        // Application.Exit() / 进程退出 → 允许关闭
        if (e.CloseReason == CloseReason.ApplicationExitCall
            || e.CloseReason == CloseReason.TaskManagerClosing
            || e.CloseReason == CloseReason.WindowsShutDown)
        {
            _refreshTimer.Stop();
            return;
        }
        // 用户点击 X 按钮 → 隐藏到托盘
        e.Cancel = true;
        Hide();
        AddLog("窗口已隐藏至系统托盘，左键托盘图标可重新打开");
    }

    /// <summary>最小化时 → 隐藏到托盘（服务端窗口不应占据任务栏）。</summary>
    private void Window_Resize(object? sender, EventArgs e)
    {
        if (WindowState == FormWindowState.Minimized)
        {
            Hide();
            AddLog("窗口已最小化至系统托盘");
        }
    }

    /// <summary>首次显示时：刷新数据 + 刷入缓存日志。</summary>
    private async void Window_Shown(object? sender, EventArgs e)
    {
        _logs.Flush();
        await _devices.RefreshAsync();
    }
}
