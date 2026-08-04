using CloudPan.Infrastructure.Design;
using CloudPan.Server.Core;

namespace CloudPan.Server.UI;

/// <summary>
/// 服务端管理窗口——显示运行状态、设备列表、最近日志。
/// 布局/设备列表/日志汇流/统计卡外提为 ServerWindowLayout/ServerDeviceListView/ServerLogSink/ServerStatCards 协作类（T-110）。
/// </summary>
public partial class ServerWindow : Form
{
    internal Label _statusLabel = null!;
    internal Label _uptimeLabel = null!;
    internal Label _connLabel = null!;
    internal ListView _deviceList = null!;
    internal ListBox _logList = null!;
    internal Button _clearLogBtn = null!;
    internal Panel _emptyStatePanel = null!;
    internal Label _emptyIcon = null!;
    internal Label _emptyTitle = null!;
    internal Label _emptyHint = null!;
    internal readonly IServerStatusService _statusService;
    internal System.Windows.Forms.Timer _refreshTimer = null!;
    internal readonly DateTime _startTime = DateTime.UtcNow;
    internal System.Windows.Forms.TabControl _tabs = null!;
    internal SettingsPage _settingsPage = null!;

    /// <summary>
    /// 窗口句柄创建前缓存的消息（AddLog 在窗口首次 Show 前被调用时暂存于此）。
    /// 句柄创建后自动刷入日志列表。
    /// </summary>
    internal readonly List<string> _pendingLogs = new();

    // T-110：布局/设备列表/日志汇流外提协作类（只存引用，惰性访问控件）
    private readonly ServerWindowLayout _layout;
    private readonly ServerDeviceListView _devices;
    private readonly ServerLogSink _logs;

    public ServerWindow(IServerStatusService statusService, ITokenService tokenService, int effectivePort, string currentSyncRoot)
    {
        _statusService = statusService;
        Text = "CloudPan 服务端 — 管理";
        Size = new Size(720, 520);
        MinimumSize = new Size(600, 400);
        StartPosition = FormStartPosition.CenterScreen;
        Icon = IconFactory.CreateServer();
        Font = new Font(CloudPanFonts.FontFamily, CloudPanFonts.SizeBody);
        BackColor = CloudPanColors.BackgroundWhite;

        // 职责外提协作类（T-110）：只存引用，控件由布局协作类惰性构建
        _logs = new ServerLogSink(this);
        _devices = new ServerDeviceListView(this);
        _layout = new ServerWindowLayout(this);
        _layout.Build(tokenService, statusService, effectivePort, currentSyncRoot);

        // ===== 事件订阅（具名处理器保留在声明类，CP301） =====
        _refreshTimer.Tick += RefreshTimer_Tick;
        _refreshTimer.Start();

        _clearLogBtn.Click += ClearLogBtn_Click;
        _emptyStatePanel.Resize += EmptyStatePanel_Resize;

        // 关闭按钮 → 隐藏到托盘（而非销毁窗口）
        FormClosing += Window_FormClosing;

        // 最小化时 → 隐藏到托盘（服务端窗口不应占据任务栏）
        Resize += Window_Resize;

        // 首次显示时：刷新数据 + 刷入缓存日志
        Shown += Window_Shown;

        // T-032 深色模式：接入主题跟随（当前主题归一化 + 系统切换时刷新，含内部 SettingsPage 树）
        ThemeWatcher.Watch(this);
    }

    /// <summary>切换到"设置"页签（托盘"设置"菜单入口）。</summary>
    public void OpenSettingsTab()
    {
        foreach (TabPage page in _tabs.TabPages)
        {
            if (page.Text == "设置")
            {
                _tabs.SelectedTab = page;
                break;
            }
        }
    }

    /// <summary>追加运行日志（线程安全）。逻辑经 ServerLogSink 外提（T-110）。</summary>
    public void AddLog(string msg) => _logs.Append(msg);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _refreshTimer.Stop();
            _refreshTimer.Dispose();
        }
        base.Dispose(disposing);
    }
}
