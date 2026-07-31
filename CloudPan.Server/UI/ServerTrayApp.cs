using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using CloudPan.Shared;
using Microsoft.Win32;

namespace CloudPan.Server.UI;

/// <summary>
/// 服务端托盘——管理窗口、Web 服务生命周期、Token 管理。
/// </summary>
public class ServerTrayApp : ApplicationContext
{
    private readonly NotifyIcon _trayIcon;
    private readonly ServerWindow _window;
    private readonly WebApplication _app;
    private readonly string _serverUrl;

    /// <summary>服务端 Token（供托盘菜单复制）。</summary>
    public static string? Token { get; set; }

    public ServerTrayApp(WebApplication app, ServerWindow window)
    {
        _app = app;
        _window = window;

        // 获取本机 URL
        string host = Dns.GetHostName();
        string ip = "";
        try
        {
            ip = Dns.GetHostEntry(host).AddressList
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)?.ToString() ?? host;
        }
        catch { ip = host; }
        // 从配置读取协议：Phase 0 用 HTTP，后续启用 HTTPS 时配置 Kestrel:Endpoints:Https:Enabled
        bool useHttps = _app.Configuration.GetValue<bool>("Kestrel:Endpoints:Https:Enabled");
        string scheme = useHttps ? "https" : "http";
        _serverUrl = $"{scheme}://{ip}:{SpecPorts.HttpPort}";

        _trayIcon = new NotifyIcon
        {
            Icon = CloudPan.Shared.UI.ServerIcons.CreateServer(),
            Text = $"CloudPan Server — {_serverUrl}",
            Visible = true,
            ContextMenuStrip = new ContextMenuStrip()
        };

        // 菜单项
        _trayIcon.ContextMenuStrip.Items.Add("复制服务端地址", null, (_, _) => CopyToClipboard(_serverUrl));
        _trayIcon.ContextMenuStrip.Items.Add("复制 Token", null, (_, _) => CopyToClipboard(Token ?? "未生成"));
        _trayIcon.ContextMenuStrip.Items.Add("显示 Token", null, (_, _) => ShowToken());
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
        _trayIcon.ContextMenuStrip.Items.Add("显示管理窗口", null, (_, _) => ShowWindow());
        _trayIcon.ContextMenuStrip.Items.Add("打开同步文件夹", null, (_, _) => OpenFolder());
        _trayIcon.ContextMenuStrip.Items.Add("打开日志目录", null, (_, _) => OpenLogDir());
        _trayIcon.ContextMenuStrip.Items.Add(new ToolStripSeparator());
        _trayIcon.ContextMenuStrip.Items.Add("停止服务", null, (_, _) => Shutdown());

        _trayIcon.DoubleClick += (_, _) => ShowWindow();

        // 定时更新托盘文字
        System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 5000 };
        timer.Tick += (_, _) =>
        {
            _trayIcon.Text = $"CloudPan Server — {_serverUrl}";
        };
        timer.Start();

        // 启动气泡提示
        _trayIcon.ShowBalloonTip(5000, "CloudPan 服务已启动",
            $"地址: {_serverUrl}\n右键托盘图标管理", ToolTipIcon.Info);
    }

    private static void CopyToClipboard(string text)
    {
        try { Clipboard.SetText(text); }
        catch (Exception ex)
        {
            MessageBox.Show($"复制到剪贴板失败: {ex.Message}\n请手动复制。", "CloudPan",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void ShowToken()
    {
        if (Token == null)
        {
            MessageBox.Show("Token 尚未生成。请检查服务端是否正常启动。", "CloudPan");
            return;
        }
        MessageBox.Show($"家庭共享 Token:\n\n{Token}\n\n请将此 Token 输入客户端配置中。\n提示：右键菜单可一键复制。",
            "CloudPan — Token", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void ShowWindow()
    {
        _window.Show();
        _window.WindowState = FormWindowState.Normal;
        _window.Activate();
    }

    private void OpenFolder()
    {
        string syncRoot = _app.Configuration.GetValue<string>("SyncRoot")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "CloudPan");
        try { Process.Start("explorer.exe", syncRoot); }
        catch (Exception ex) { _window.AddLog($"打开文件夹失败: {ex.Message}"); }
    }

    private void OpenLogDir()
    {
        string logDir = Path.Combine(
            _app.Configuration.GetValue<string>("SyncRoot")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "CloudPan"),
            ".cloudpan", "logs");
        try
        {
            if (Directory.Exists(logDir))
            {
                Process.Start("explorer.exe", logDir);
            }
            else
            {
                _window.AddLog("日志目录尚不存在");
            }
        }
        catch (Exception ex) { _window.AddLog($"打开日志目录失败: {ex.Message}"); }
    }

    private static bool IsAutoStartEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", false);
            return key?.GetValue("CloudPanServerTray") != null;
        }
        catch { return false; }
    }

    private static void SetAutoStart(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", true);
            if (key == null)
            {
                return;
            }

            if (enable)
            {
                key.SetValue("CloudPanServerTray", $"\"{Application.ExecutablePath}\" --tray");
            }
            else
            {
                key.DeleteValue("CloudPanServerTray", false);
            }
        }
        catch (Exception ex) { Debug.WriteLine($"设置开机自启失败: {ex.Message}"); }
    }

    private async void Shutdown()
    {
        var result = MessageBox.Show("确定要停止 CloudPan 服务吗？\n所有客户端将断开连接。",
            "确认停止", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (result != DialogResult.Yes)
        {
            return;
        }

        _trayIcon.Visible = false;
        _window.AddLog("正在关闭服务...");
        try
        {
            await _app.StopAsync();
        }
        catch (Exception ex)
        {
            _window.AddLog($"停止服务时异常: {ex.Message}");
        }
        Application.Exit();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _trayIcon.Dispose();
            _window.Dispose();
        }
        base.Dispose(disposing);
    }
}
