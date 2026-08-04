using CloudPan.Client.Core.Models;
using CloudPan.Client.Core.Services;
using CloudPan.Infrastructure.Design;

namespace CloudPan.Client.UI;

/// <summary>
/// 系统托盘应用上下文——管理托盘图标和右键菜单。
/// 菜单/动作/事件状态外提为 TrayMenu/TrayActions/TrayEventHandlers 协作类（T-109）。
/// </summary>
public partial class TrayAppContext : ApplicationContext
{
    public static NotifyIcon? TrayIcon { get; private set; }

    internal readonly NotifyIcon _trayIcon;
    internal readonly MainWindow _mainWindow;
    internal readonly Icon _normalIcon;
    private readonly Task _syncTask;
    private readonly Task _wsTask;
    internal readonly CancellationTokenSource _cts = new();
    internal readonly SyncEngine _engine;
    private readonly WebSocketClient _wsClient;
    internal readonly CloudPan.Contract.IApiClient _api;
    internal readonly ClientConfig _config;
    internal readonly System.Collections.Concurrent.ConcurrentQueue<string> _conflictPaths = new();
    internal readonly System.Threading.SynchronizationContext? _syncCtx; // UI 同步上下文（构造函数捕获，供具名事件处理器）
    internal readonly System.Collections.Concurrent.ConcurrentQueue<string> _recentActivity = new(); // 最近同步活动（托盘文本）
    internal volatile bool _isPaused;

    /// <summary>重配引导已提示过（F-34/T-034）：防 HTTP 队列与 WebSocket 双重 401 同时弹两次；连接恢复/重配成功后重置。</summary>
    internal volatile bool _reconfigPromptShown;

    // T-109：托盘职责外提协作类
    internal readonly TrayActions _actions;
    internal readonly TrayEventHandlers _events;
    internal readonly TrayMenu _menu;

    public TrayAppContext(SyncEngine engine, WebSocketClient wsClient, CloudPan.Contract.IApiClient api, ClientConfig config)
    {
        _engine = engine;
        _wsClient = wsClient;
        _api = api;
        _config = config;
        _mainWindow = new MainWindow(engine, config, api);

        _actions = new TrayActions(this);
        _events = new TrayEventHandlers(this);
        _menu = new TrayMenu(this);

        // ===== 托盘图标 =====
        // 不设 ContextMenuStrip，避免拦截鼠标事件。
        // 左键→窗口 / 右键→动态构建菜单
        _trayIcon = new NotifyIcon
        {
            Icon = IconFactory.CreateClient(),
            Text = "CloudPan — 文件同步",
            Visible = true
        };
        TrayIcon = _trayIcon;
        _normalIcon = IconFactory.CreateClient();

        _trayIcon.MouseUp += _events.TrayIcon_MouseUp;

        // 捕获 UI 线程同步上下文（提升为字段，供具名事件处理器使用）
        _syncCtx = System.Threading.SynchronizationContext.Current;

        // 启动同步引擎
        _syncTask = Task.Run(() => engine.StartAsync(_cts.Token));
        _syncTask.ContinueWith(t =>
        {
            if (t.IsFaulted)
            {
                var ex = t.Exception?.InnerException ?? t.Exception;
                string msg = ex?.Message ?? "未知错误";
                _syncCtx?.Post(_ =>
                {
                    _trayIcon.ShowBalloonTip(10000, "CloudPan — 同步异常",
                        $"同步引擎已停止: {msg}\n请检查网络或重新启动客户端。", ToolTipIcon.Error);
                    _trayIcon.Icon = SystemIcons.Error;
                    _trayIcon.Text = "CloudPan — 同步异常";
                }, null);
            }
        }, TaskContinuationOptions.OnlyOnFaulted);

        // 启动 WebSocket 客户端
        _wsTask = Task.Run(() => wsClient.StartAsync(_cts.Token));
        _wsTask.ContinueWith(t =>
        {
            if (t.IsFaulted)
            {
                var ex = t.Exception?.InnerException ?? t.Exception;
                string msg = ex?.Message ?? "未知错误";
                _syncCtx?.Post(_ =>
                {
                    _trayIcon.ShowBalloonTip(10000, "CloudPan — 连接异常",
                        $"服务端连接已断开: {msg}\n客户端将自动重连。", ToolTipIcon.Warning);
                }, null);
            }
        }, TaskContinuationOptions.OnlyOnFaulted);

        // 冲突检测 → 托盘气泡 + 警告图标
        engine.ConflictDetected += _events.OnConflictDetected;
        // 冲突解决
        engine.ConflictResolved += _events.OnConflictResolved;
        // 断连 / 重连通知
        wsClient.OnDisconnected += _events.OnWsDisconnected;
        wsClient.OnConnected += _events.OnWsConnected;
        // 认证失败
        wsClient.OnPermanentFailure += _events.OnWsPermanentFailure;
        // F-34/T-034：连续 401（Token 或服务端配置已变更）→ 重配引导
        engine.ReconfigurationRequired += _events.OnReconfigurationRequired;
        // 状态更新
        engine.StatusChanged += _events.OnStatusChanged;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cts.Cancel();
            _engine.Stop();        // 停止同步引擎（含 FileWatcher、信号量释放）
            _wsClient.Stop();      // 停止 WebSocket 连接
            _cts.Dispose();
            _trayIcon.Dispose();
            _normalIcon.Dispose();
            _mainWindow.Dispose();
            _engine.ReconfigurationRequired -= _events.OnReconfigurationRequired; // 退订重配引导事件（CP300）
            _engine.Dispose();     // 释放 SyncEngine（取消 WS 事件订阅、释放 _syncLock、_fileWatcher）
            _wsClient.Dispose();   // 释放 WebSocketClient（Socket、信号量、事件委托）
        }
        base.Dispose(disposing);
    }

    internal void ShowWindow()
    {
        _mainWindow.Show();
        _mainWindow.WindowState = FormWindowState.Normal;
        _mainWindow.Activate();
    }
}
