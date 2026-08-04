using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using CloudPan.Contract;
using CloudPan.Infrastructure.Design;

namespace CloudPan.Client.UI;

/// <summary>配置窗口局域网发现协作类（T-109）：UDP 搜索服务端、搜索动画与浏览文件夹。</summary>
internal sealed class SetupWizardDiscovery
{
    private static readonly TimeSpan SearchTimeout = TimeSpan.FromSeconds(5);
    private static readonly string[] SearchSpinner = ["|", "/", "-", "\\"];

    private readonly SetupForm _form;

    public SetupWizardDiscovery(SetupForm form)
    {
        _form = form;
    }

    // ════════════════════════════════════════════════════════════════
    //  局域网搜索（UDP 广播）
    // ════════════════════════════════════════════════════════════════

    /// <summary>搜索局域网内的 CloudPan 服务端（不再在窗口加载时自动触发）。</summary>
    public async Task SearchLanAsync()
    {
        _form._isSearching = true;
        _form._searchButton.Enabled = false;
        _form._searchAnimFrame = 0;
        _form._searchAnimTimer.Start();
        _form._progressBar.Visible = true;
        _form._statusLabel.Text = "正在搜索局域网服务端...";
        _form._statusLabel.ForeColor = CloudPanColors.TextMuted;
        _form._urlStatusIcon.Text = "○";
        _form._urlStatusIcon.ForeColor = CloudPanColors.TextMuted;

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
                    _form._serverUrlBox.Text = server;
                    _form._urlStatusIcon.Text = "✓";
                    _form._urlStatusIcon.ForeColor = CloudPanColors.SuccessGreen;
                    _form._statusLabel.Text = "已找到服务端: " + (name ?? server);
                    _form._statusLabel.ForeColor = CloudPanColors.SuccessGreen;
                    _form._searchFound = true; // 阻止 TextChanged 重置状态
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
            _form._searchAnimTimer.Stop();
            _form._searchButton.Text = "搜索局域网";
            _form._searchButton.Enabled = true;
            _form._progressBar.Visible = false;
            _form._isSearching = false;
        }

        if (found)
        {
            return; // 已设置成功状态
        }

        if (errorMessage != null)
        {
            _form._statusLabel.Text = errorMessage;
            _form._statusLabel.ForeColor = CloudPanColors.ErrorRed;
        }
        else
        {
            _form._statusLabel.Text = "未找到服务端。请在台式机上右键托盘图标 → 复制服务端地址并粘贴到上方";
            _form._statusLabel.ForeColor = CloudPanColors.WarningOrange;
        }
    }

    // ================================================================
    // 搜索与地址编辑具名事件处理器（CP301：避免匿名 lambda 订阅无法退订）
    // ================================================================

    public async void SearchButton_Click(object? sender, EventArgs e) => await SearchLanAsync();

    public void SearchAnimTimer_Tick(object? sender, EventArgs e)
    {
        _form._searchAnimFrame = (_form._searchAnimFrame + 1) % SearchSpinner.Length;
        _form._searchButton.Text = "搜索中 " + SearchSpinner[_form._searchAnimFrame];
    }

    public void ServerUrlBox_TextChanged(object? sender, EventArgs e)
    {
        if (!_form._isSearching)
        {
            if (_form._searchFound)
            {
                // 搜索成功后用户手动改写 → 清除搜索状态，让图标重新变为空心
                _form._searchFound = false;
            }
            _form._urlStatusIcon.Text = "○";
            _form._urlStatusIcon.ForeColor = CloudPanColors.TextMuted;
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  浏览文件夹
    // ════════════════════════════════════════════════════════════════

    public void OnBrowseClick(object? sender, EventArgs e)
    {
        using FolderBrowserDialog d = new FolderBrowserDialog
        {
            SelectedPath = _form._syncRootBox.Text,
            ShowNewFolderButton = true,
        };
        if (d.ShowDialog() == DialogResult.OK)
        {
            _form._syncRootBox.Text = d.SelectedPath;
        }
    }
}
