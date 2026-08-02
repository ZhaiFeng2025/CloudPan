using System.Drawing.Drawing2D;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using CloudPan.Contract;
using CloudPan.Infrastructure.Design;

namespace CloudPan.Client.UI;

/// <summary>SetupForm 部分类：局域网搜索发现服务端与浏览文件夹。</summary>
public partial class SetupForm
{

    // ════════════════════════════════════════════════════════════════
    //  局域网搜索（UDP 广播）
    // ════════════════════════════════════════════════════════════════

    /// <summary>搜索局域网内的 CloudPan 服务端（不再在窗口加载时自动触发）。</summary>
    private async Task SearchLanAsync()
    {
        _isSearching = true;
        _searchButton.Enabled = false;
        _searchAnimFrame = 0;
        _searchAnimTimer.Start();
        _progressBar.Visible = true;
        _statusLabel.Text = "正在搜索局域网服务端...";
        _statusLabel.ForeColor = CloudPanColors.TextMuted;
        _urlStatusIcon.Text = "○";
        _urlStatusIcon.ForeColor = CloudPanColors.TextMuted;

        bool found = false;
        string? errorMessage = null;

        try
        {
            using UdpClient udp = new UdpClient();
            udp.EnableBroadcast = true;
            byte[] request = Encoding.UTF8.GetBytes("CLOUDPAN_DISCOVER");
            await udp.SendAsync(request, request.Length, new IPEndPoint(IPAddress.Broadcast, SpecPorts.UdpDiscoveryPort));

            using CancellationTokenSource cts = new CancellationTokenSource(SearchTimeout);
            try
            {
                var result = await udp.ReceiveAsync(cts.Token);
                string json = Encoding.UTF8.GetString(result.Buffer);

                using JsonDocument doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                string? server = root.TryGetProperty("server", out var sProp) ? sProp.GetString() : null;
                string? name = root.TryGetProperty("name", out var nProp) ? nProp.GetString() : null;

                if (!string.IsNullOrEmpty(server))
                {
                    _serverUrlBox.Text = server;
                    _urlStatusIcon.Text = "✓";
                    _urlStatusIcon.ForeColor = CloudPanColors.SuccessGreen;
                    _statusLabel.Text = "已找到服务端: " + (name ?? server);
                    _statusLabel.ForeColor = CloudPanColors.SuccessGreen;
                    _searchFound = true; // 阻止 TextChanged 重置状态
                    found = true;
                }
            }
            catch (OperationCanceledException) { /* 超时 —— 显示未找到 */ }
            catch (JsonException)
            {
                // 非 JSON 响应（可能来自其他设备广播），静默忽略
                System.Diagnostics.Debug.WriteLine("[SetupForm] 搜索收到非 JSON 响应");
            }
        }
        catch (SocketException)
        {
            errorMessage = "网络搜索异常，请检查防火墙或手动输入地址";
        }
        catch (Exception ex)
        {
            errorMessage = $"网络搜索异常: {ex.Message}";
        }
        finally
        {
            _searchAnimTimer.Stop();
            _searchButton.Text = "搜索局域网";
            _searchButton.Enabled = true;
            _progressBar.Visible = false;
            _isSearching = false;
        }

        if (found)
        {
            return; // 已设置成功状态
        }

        if (errorMessage != null)
        {
            _statusLabel.Text = errorMessage;
            _statusLabel.ForeColor = CloudPanColors.ErrorRed;
        }
        else
        {
            _statusLabel.Text = "未找到服务端。请在台式机上右键托盘图标 → 复制服务端地址并粘贴到上方";
            _statusLabel.ForeColor = CloudPanColors.WarningOrange;
        }
    }

    // ================================================================
    // 搜索与地址编辑具名事件处理器（CP301：避免匿名 lambda 订阅无法退订）
    // ================================================================

    private async void SearchButton_Click(object? sender, EventArgs e) => await SearchLanAsync();

    private void SearchAnimTimer_Tick(object? sender, EventArgs e)
    {
        _searchAnimFrame = (_searchAnimFrame + 1) % SearchSpinner.Length;
        _searchButton.Text = "搜索中 " + SearchSpinner[_searchAnimFrame];
    }

    private void ServerUrlBox_TextChanged(object? sender, EventArgs e)
    {
        if (!_isSearching)
        {
            if (_searchFound)
            {
                // 搜索成功后用户手动改写 → 清除搜索状态，让图标重新变为空心
                _searchFound = false;
            }
            _urlStatusIcon.Text = "○";
            _urlStatusIcon.ForeColor = CloudPanColors.TextMuted;
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  浏览文件夹
    // ════════════════════════════════════════════════════════════════

    private void OnBrowseClick(object? sender, EventArgs e)
    {
        using FolderBrowserDialog d = new FolderBrowserDialog
        {
            SelectedPath = _syncRootBox.Text,
            ShowNewFolderButton = true,
        };
        if (d.ShowDialog() == DialogResult.OK)
        {
            _syncRootBox.Text = d.SelectedPath;
        }
    }
}
