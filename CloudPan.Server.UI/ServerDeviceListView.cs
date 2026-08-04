using CloudPan.Contract;
using CloudPan.Infrastructure.Design;
using CloudPan.Server.Core;

namespace CloudPan.Server.UI;

/// <summary>管理窗口设备列表协作类（T-110）：定时刷新设备状态（经 IServerStatusService 查询）与空状态居中布局。逻辑从 ServerWindow 外提。</summary>
internal sealed class ServerDeviceListView
{
    private readonly ServerWindow _form;

    public ServerDeviceListView(ServerWindow form)
    {
        _form = form;
    }

    internal async Task RefreshAsync()
    {
        try
        {
            var elapsed = DateTime.UtcNow - _form._startTime;
            _form._uptimeLabel.Text = $"{(int)elapsed.TotalHours}h {elapsed.Minutes}m";
            _form._uptimeLabel.ForeColor = CloudPanColors.SuccessGreen;
            _form._statusLabel.ForeColor = CloudPanColors.SuccessGreen;

            List<AdminDeviceItem> devices = (await _form._statusService.GetDevicesAsync()).Take(20).ToList();

            _form._deviceList.BeginUpdate();
            _form._deviceList.Items.Clear();

            if (devices.Count == 0)
            {
                // 显示空状态引导
                _form._deviceList.Visible = false;
                _form._emptyStatePanel.Visible = true;
                _form._emptyStatePanel.BringToFront();
                CenterEmptyState();
            }
            else
            {
                // 显示设备列表
                _form._emptyStatePanel.Visible = false;
                _form._deviceList.Visible = true;
                _form._deviceList.BringToFront();

                foreach (var d in devices)
                {
                    bool isServer = d.Id == "server";
                    ListViewItem item = new ListViewItem(d.Name) { Tag = d };
                    item.SubItems.Add(isServer ? "服务端" : "客户端");
                    item.SubItems.Add(d.Online == 1 ? "在线" : "离线");
                    item.SubItems.Add(DateTime.TryParse(d.LastSeen, out var dt)
                        ? dt.ToLocalTime().ToString("MM-dd HH:mm") : "-");
                    item.SubItems.Add("-");  // 同步文件数（简化实现）
                    _form._deviceList.Items.Add(item);
                }
            }

            _form._deviceList.EndUpdate();

            _form._connLabel.Text = devices.Count(d => d.Online == 1).ToString();
        }
        catch (Exception ex)
        {
            _form.AddLog($"刷新数据失败: {ex.Message}（5 秒后自动重试）");
            _form._statusLabel.ForeColor = CloudPanColors.ErrorRed;
        }
    }

    /// <summary>
    /// 居中空状态面板内的图标和文字
    /// </summary>
    internal void CenterEmptyState()
    {
        if (_form._emptyStatePanel.Width <= 0 || _form._emptyStatePanel.Height <= 0)
        {
            return;
        }

        int cx = _form._emptyStatePanel.Width / 2;
        int cy = _form._emptyStatePanel.Height / 2;
        _form._emptyIcon.Location = new Point(cx - _form._emptyIcon.Width / 2, cy - 70);
        _form._emptyTitle.Location = new Point(cx - _form._emptyTitle.Width / 2, cy - 10);
        _form._emptyHint.Location = new Point(cx - _form._emptyHint.Width / 2, cy + 20);
    }
}
