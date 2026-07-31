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
    private ToolStripMenuItem? _autoStartItem;

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
        bool useHttps = _app.Configuration.GetValue<bool>("Kestrel:Endpoints:Https:Enabled");
        string scheme = useHttps ? "https" : "http";
        _serverUrl = $"{scheme}://{ip}:{SpecPorts.HttpPort}";

        // ===== 托盘图标 =====
        // 不设 ContextMenuStrip，因为它会拦截鼠标事件，导致 Click/DoubleClick 不触发。
        // 所有交互通过 MouseUp 手动分发：左键→窗口 / 右键→菜单。
        _trayIcon = new NotifyIcon
        {
            Icon = CloudPan.Shared.UI.ServerIcons.CreateServer(),
            Text = $"CloudPan Server — {_serverUrl}",
            Visible = true
        };

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

        // 定时更新托盘文字
        System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 5000 };
        timer.Tick += (_, _) =>
        {
            _trayIcon.Text = $"CloudPan Server — {_serverUrl}";
        };
        timer.Start();

        // 启动气泡提示
        _trayIcon.ShowBalloonTip(5000, "CloudPan 服务已启动",
            $"地址: {_serverUrl}\n左键打开管理窗口 | 右键显示菜单", ToolTipIcon.Info);
    }

    // ===== 右键菜单（运行时动态构建，避免 ContextMenuStrip 属性拦截鼠标） =====

    private void ShowTrayMenu()
    {
        var menu = new ContextMenuStrip();

        menu.Items.Add("复制服务端地址", null, (_, _) => CopyToClipboard(_serverUrl));
        menu.Items.Add("复制 Token", null, (_, _) => CopyToClipboard(Token ?? "未生成"));
        menu.Items.Add("显示 Token", null, (_, _) => ShowToken());
        menu.Items.Add(new ToolStripSeparator());

        _autoStartItem = new ToolStripMenuItem("开机自动启动")
        {
            CheckOnClick = true,
            Checked = IsAutoStartEnabled()
        };
        _autoStartItem.Click += (_, _) =>
        {
            bool newState = !IsAutoStartEnabled();
            SetAutoStart(newState);
            if (_autoStartItem != null) _autoStartItem.Checked = newState;
        };
        menu.Items.Add(_autoStartItem);

        menu.Items.Add("显示管理窗口", null, (_, _) => ShowWindow());
        menu.Items.Add("打开同步文件夹", null, (_, _) => OpenFolder());
        menu.Items.Add("打开日志目录", null, (_, _) => OpenLogDir());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("停止服务", null, (_, _) => Shutdown());

        menu.Show(Cursor.Position);
    }

    // ===== 动作方法 =====

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
            if (key == null) return;

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
        if (result != DialogResult.Yes) return;

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
