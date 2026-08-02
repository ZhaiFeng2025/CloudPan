using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using CloudPan.Server.Services;
using CloudPan.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
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
    private readonly string _syncRoot;
    private readonly ContextMenuStrip _trayMenu;
    private readonly ToolStripMenuItem _autoStartItem;
    private bool _shuttingDown;

    /// <summary>服务端 Token（供托盘菜单复制）。</summary>
    public static string? Token { get; set; }

    public ServerTrayApp(WebApplication app, ServerWindow window, int effectivePort)
    {
        _app = app;
        _window = window;

        // 启动期设置解析（与服务端 Program.cs 同链：CLI → server-settings.json → 默认）
        (string syncRoot, int port) = StartupSettingsResolver.Resolve(
            app.Configuration.GetValue<string>("SyncRoot"),
            app.Configuration.GetValue<int?>("Port"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "CloudPan"));
        _syncRoot = syncRoot;

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
        _serverUrl = $"{scheme}://{ip}:{port}";

        // ===== 预构建右键菜单（复用，避免每次动态创建导致 GDI 泄漏） =====
        _trayMenu = new ContextMenuStrip();

        _trayMenu.Items.Add("复制服务端地址", null, (_, _) => CopyToClipboard(_serverUrl));
        _trayMenu.Items.Add("复制 Token", null, (_, _) => CopyToClipboard(GetToken() ?? "未生成"));
        _trayMenu.Items.Add("显示 Token", null, (_, _) => ShowToken());
        _trayMenu.Items.Add(new ToolStripSeparator());

        _autoStartItem = new ToolStripMenuItem("开机自动启动")
        {
            Checked = IsAutoStartEnabled()
        };
        _autoStartItem.Click += AutoStartItem_Click;
        _trayMenu.Items.Add(_autoStartItem);

        _trayMenu.Items.Add("设置", null, (_, _) => { ShowWindow(); _window.OpenSettingsTab(); });
        _trayMenu.Items.Add("显示管理窗口", null, (_, _) => ShowWindow());
        _trayMenu.Items.Add("打开同步文件夹", null, (_, _) => OpenFolder());
        _trayMenu.Items.Add("打开日志目录", null, (_, _) => OpenLogDir());
        _trayMenu.Items.Add(new ToolStripSeparator());
        _trayMenu.Items.Add("停止服务", null, (_, _) => Shutdown());

        // ===== 托盘图标 =====
        // 标准 WinForms 模式：ContextMenuStrip 属性处理右键菜单，
        // MouseClick 事件处理左键（仅当 e.Button == Left 时）。
        // 注意：不监听 Click/DoubleClick——设置 ContextMenuStrip 后
        // Click 事件触发行为因 Windows 版本而异，MouseClick 最稳定。
        _trayIcon = new NotifyIcon
        {
            Icon = CloudPan.Shared.UI.ServerIcons.CreateServer(),
            Text = $"CloudPan Server — {_serverUrl}",
            Visible = true,
            ContextMenuStrip = _trayMenu
        };

        _trayIcon.MouseClick += TrayIcon_MouseClick;

        // 启动气泡提示（Win11 上可能不显示，非致命）
        _trayIcon.ShowBalloonTip(5000, "CloudPan 服务已启动",
            $"地址: {_serverUrl}\n左键打开管理窗口 | 右键显示菜单", ToolTipIcon.Info);
    }

    // ===== 动作方法 =====

    /// <summary>
    /// 复制文本到剪贴板，失败时显示警告。
    /// </summary>
    private static void CopyToClipboard(string text)
    {
        try { Clipboard.SetText(text); }
        catch (Exception ex)
        {
            MessageBox.Show($"复制到剪贴板失败: {ex.Message}\n请手动复制。", "CloudPan",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>开机自启菜单项点击：切换并持久化自启状态。</summary>
    private void AutoStartItem_Click(object? sender, EventArgs e)
    {
        bool newState = !IsAutoStartEnabled();
        SetAutoStart(newState);
        _autoStartItem.Checked = newState;
    }

    /// <summary>托盘左键点击：显示管理窗口。</summary>
    private void TrayIcon_MouseClick(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            ShowWindow();
        }
    }

    /// <summary>
    /// 获取当前 Token。优先静态缓存（Token 轮换时由设置页更新，保证托盘立即显示新值）；
    /// 未缓存时读 token.txt（T-015 后首次启动不再写静态属性，DatabaseInitializer 已把 Token 写入 token.txt）。
    /// </summary>
    private string? GetToken()
    {
        return Token ?? SecretStore.ReadToken(_syncRoot);
    }

    private void ShowToken()
    {
        string? token = GetToken();
        if (token == null)
        {
            MessageBox.Show("Token 尚未生成。请检查服务端是否正常启动。", "CloudPan");
            return;
        }
        MessageBox.Show($"家庭共享 Token:\n\n{token}\n\n请将此 Token 输入客户端配置中。\n提示：右键菜单可一键复制。",
            "CloudPan — Token", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    /// <summary>
    /// 显示管理窗口。先恢复窗口状态再显示，避免最小化后隐藏→显示时窗口仍处于最小化状态。
    /// </summary>
    private void ShowWindow()
    {
        // 先设置窗口状态，再显示——顺序重要：
        // 如果窗口之前最小化后被隐藏，Show() 可能恢复为最小化状态。
        if (_window.WindowState == FormWindowState.Minimized)
        {
            _window.WindowState = FormWindowState.Normal;
        }
        _window.Show();
        _window.WindowState = FormWindowState.Normal;
        _window.Activate();
    }

    private void OpenFolder()
    {
        try { Process.Start("explorer.exe", _syncRoot); }
        catch (Exception ex) { _window.AddLog($"打开文件夹失败: {ex.Message}"); }
    }

    private void OpenLogDir()
    {
        string logDir = Path.Combine(_syncRoot, ".cloudpan", "logs");
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

    /// <summary>
    /// 停止服务。使用 _shuttingDown 标志防止重复触发。
    /// </summary>
    private async void Shutdown()
    {
        if (_shuttingDown) return;
        _shuttingDown = true;

        var result = MessageBox.Show("确定要停止 CloudPan 服务吗？\n所有客户端将断开连接。",
            "确认停止", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (result != DialogResult.Yes)
        {
            _shuttingDown = false;
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
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayMenu.Dispose();
            _window.Dispose();
        }
        base.Dispose(disposing);
    }
}
