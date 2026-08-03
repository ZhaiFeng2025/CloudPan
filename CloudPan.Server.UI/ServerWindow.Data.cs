using CloudPan.Infrastructure.Design;
using CloudPan.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CloudPan.Server.UI;

/// <summary>ServerWindow 部分类：设备状态刷新（EF 查询）与线程安全日志追加。</summary>
public partial class ServerWindow
{
    private async Task RefreshDataAsync()
    {
        try
        {
            var elapsed = DateTime.UtcNow - _startTime;
            _uptimeLabel.Text = $"{(int)elapsed.TotalHours}h {elapsed.Minutes}m";
            _uptimeLabel.ForeColor = CloudPanColors.SuccessGreen;
            _statusLabel.ForeColor = CloudPanColors.SuccessGreen;

            await using var db = await _dbFactory.CreateDbContextAsync();
            var devices = await db.Devices.OrderByDescending(d => d.LastSeen).Take(20).ToListAsync();

            _deviceList.BeginUpdate();
            _deviceList.Items.Clear();

            if (devices.Count == 0)
            {
                // 显示空状态引导
                _deviceList.Visible = false;
                _emptyStatePanel.Visible = true;
                _emptyStatePanel.BringToFront();
                CenterEmptyState();
            }
            else
            {
                // 显示设备列表
                _emptyStatePanel.Visible = false;
                _deviceList.Visible = true;
                _deviceList.BringToFront();

                foreach (var d in devices)
                {
                    bool isServer = d.Id == "server";
                    ListViewItem item = new ListViewItem(d.Name) { Tag = d };
                    item.SubItems.Add(isServer ? "服务端" : "客户端");
                    item.SubItems.Add(d.Online == 1 ? "在线" : "离线");
                    item.SubItems.Add(DateTime.TryParse(d.LastSeen, out var dt)
                        ? dt.ToLocalTime().ToString("MM-dd HH:mm") : "-");
                    item.SubItems.Add("-");  // 同步文件数（简化实现）
                    _deviceList.Items.Add(item);
                }
            }

            _deviceList.EndUpdate();

            _connLabel.Text = devices.Count(d => d.Online == 1).ToString();
        }
        catch (Exception ex)
        {
            AddLog($"刷新数据失败: {ex.Message}（5 秒后自动重试）");
            _statusLabel.ForeColor = CloudPanColors.ErrorRed;
        }
    }

    /// <summary>
    /// 追加日志（线程安全）。窗口句柄创建前调用时缓存消息，句柄创建后自动刷入。
    /// 使用 BeginInvoke 避免死锁和窗口已释放异常。
    /// </summary>
    public void AddLog(string msg)
    {
        if (IsDisposed) return;

        // 窗口句柄尚未创建 → 缓存消息
        if (!IsHandleCreated)
        {
            _pendingLogs.Add(msg);
            return;
        }

        if (InvokeRequired)
        {
            try { BeginInvoke(() => AddLog(msg)); }
            catch (ObjectDisposedException) { /* 窗口已关闭，静默放弃 */ }
            return;
        }
        _logList.Items.Add($"[{DateTime.Now:HH:mm:ss}] {msg}");
        if (_logList.Items.Count > 500)
        {
            _logList.Items.RemoveAt(0);
        }

        _logList.TopIndex = _logList.Items.Count - 1;
    }
}
