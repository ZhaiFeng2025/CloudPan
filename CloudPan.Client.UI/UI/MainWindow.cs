using System.Diagnostics;
using System.Drawing.Drawing2D;
using CloudPan.Client.Core.Models;
using CloudPan.Client.Core.Services;
using CloudPan.Contract;
using CloudPan.Infrastructure.Design;

namespace CloudPan.Client.UI;

/// <summary>
/// 主窗口——文件浏览主视图（T-013）。顶部一条同步状态汇总（指示灯+状态+进度+速率），
/// 主区为文件浏览（面包屑/搜索/列表-网格切换/排序/每文件状态图标），日志移入可折叠侧栏（默认折叠）。
/// WinForms 实现，包含 GDI+ 发光状态指示灯、带百分比文字的进度条、统一日志过滤及系统托盘最小化。
/// </summary>
public partial class MainWindow : Form
{
    // ================================================================
    // 控件
    // ================================================================
    private GlowDot _statusDot = null!;              // GDI+ 发光状态指示灯
    private Label _statusLabel = null!;              // 状态文字（顶部一条汇总）
    private Label _statusInfo = null!;               // 状态量化信息（文件计数/传输详情）
    private Label _speedLabel = null!;               // 传输速率
    private ProgressBarWithText _progressBar = null!; // 带百分比文字的进度条
    private ListBox _logList = null!;                // 统一日志列表（可折叠侧栏内）
    private ComboBox _logFilterComboBox = null!;     // 日志过滤下拉框
    private Button _pauseButton = null!;
    private Button _openFolderButton = null!;
    private Button _retryButton = null!;
    private Button _conflictButton = null!;
    private Button _logToggleButton = null!;          // 日志侧栏开关（T-013）
    private Label _errorCountLabel = null!;          // 状态栏右侧错误计数
    private SplitContainer _splitter = null!;         // 主区：文件浏览 + 日志侧栏（T-013）
    private FileBrowserView _fileBrowser = null!;     // 文件浏览主视图（T-013）
    private System.Windows.Forms.Timer _browserRefreshTimer = null!; // 文件浏览定时刷新（T-013）
    private bool _browserRefreshBusy;                 // 防重入：刷新进行中跳过本次定时触发
    private System.Windows.Forms.Timer _searchDebounceTimer = null!; // 搜索防抖定时器（T-013）
    private int _logSidebarWidth = 320;               // 日志侧栏展开宽度（T-013）

    // T-014：删除进回收站 + 撤销（底部 Snackbar，删除后 5 秒内可撤销）
    private Panel _undoBar = null!;
    private Label _undoLabel = null!;
    private Button _undoButton = null!;
    private System.Windows.Forms.Timer _undoTimer = null!;
    private TrashItem? _lastDeletedTrashItem;

    // 文件浏览导航状态（T-013）
    private string _currentPath = "/";
    private string? _searchText;

    private readonly SyncEngine _engine;
    private bool _paused;

    // 日志列表容量上限
    private const int MaxLogItems = 500;
    private readonly List<string> _allLogEntries = new();

    // 同步进度跟踪（来自 SyncStatus）
    private long _lastTotalBytes;
    private long _lastBytesTransferred;
    private string? _lastCurrentFile;
    private int _lastFileTotal;
    private int _lastFileCompleted;
    private DateTime? _lastSyncTime;

    // ================================================================
    // 错误管理
    // ================================================================

    private readonly List<SyncErrorInfo> _errors = new();

    /// <summary>错误条目——记录失败的同步操作。</summary>
    private class SyncErrorInfo
    {
        public string FilePath { get; set; } = "";
        public string Message { get; set; } = "";
        public DateTime Timestamp { get; set; }
        public SyncOperation Operation { get; set; }
    }

    // ================================================================
    // 冲突管理
    // ================================================================

    private readonly List<(ConflictInfo Info, DateTime DetectedAt)> _conflicts = new();

    // ================================================================
    // 构造
    // ================================================================

    public MainWindow(SyncEngine engine)
    {
        _engine = engine;

        // ── 窗口属性 ──
        Text = "CloudPan — 文件同步";
        Size = new Size(980, 640);
        MinimumSize = new Size(700, 480);
        StartPosition = FormStartPosition.CenterScreen;
        Icon = IconFactory.CreateClient();
        BackColor = CloudPanColors.BackgroundWhite;
        Font = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;

        BuildLayout();
        BindEvents();

        // 文件浏览定时刷新（T-013）；5 秒周期覆盖同步/错误/冲突状态变化
        _browserRefreshTimer = new System.Windows.Forms.Timer { Interval = 5000 };
        _browserRefreshTimer.Tick += BrowserRefreshTimer_Tick;

        // 搜索防抖（T-013）：停止输入 300ms 后触发一次重载
        _searchDebounceTimer = new System.Windows.Forms.Timer { Interval = 300 };
        _searchDebounceTimer.Tick += SearchDebounceTimer_Tick;

        // 删除撤销窗口（T-014）：5 秒内可撤销，超时隐藏
        _undoTimer = new System.Windows.Forms.Timer { Interval = 5000 };
        _undoTimer.Tick += UndoTimer_Tick;
    }

    // ================================================================
    // 事件绑定
    // ================================================================

    private void BindEvents()
    {
        _engine.StatusChanged += OnStatusChanged;
        _engine.QueueProgressChanged += OnQueueProgressChanged;
        _engine.ErrorOccurred += OnErrorOccurred;
        _engine.ConflictDetected += OnConflictDetected;
        FormClosing += OnFormClosing;
        Shown += OnShown;

        // 文件浏览导航（T-013）
        _fileBrowser.DirectoryActivated += FileBrowser_DirectoryActivated;
        _fileBrowser.FileActivated += FileBrowser_FileActivated;
        _fileBrowser.UpRequested += FileBrowser_UpRequested;
        _fileBrowser.SearchTextChanged += FileBrowser_SearchTextChanged;
        _fileBrowser.StateResolver = ResolveBrowseState;

        // T-014：删除进回收站 + 最近删除入口
        _fileBrowser.DeleteRequested += FileBrowser_DeleteRequested;
        _fileBrowser.TrashRequested += FileBrowser_TrashRequested;

        // T-018：分享 + 版本历史（仅文件）
        _fileBrowser.ShareRequested += FileBrowser_ShareRequested;
        _fileBrowser.VersionHistoryRequested += FileBrowser_VersionHistoryRequested;
    }

    // ================================================================
    // 关闭行为：隐藏到托盘 + 气泡提示
    // ================================================================

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            var settings = SettingsStore.Load();
            if (!settings.TrayCloseAcknowledged)
            {
                var result = MessageBox.Show(
                    "关闭窗口后 CloudPan 将继续在后台运行，\n托盘图标仍然可见。\n\n" +
                    "以后要完全退出请右键托盘图标选择「退出」。\n\n是否继续？",
                    "CloudPan — 后台运行",
                    MessageBoxButtons.OKCancel,
                    MessageBoxIcon.Information);
                if (result != DialogResult.OK)
                {
                    e.Cancel = true;
                    return;
                }
                settings.TrayCloseAcknowledged = true;
                settings.Save();
            }

            e.Cancel = true;
            Hide();
            _browserRefreshTimer.Stop(); // 隐藏到托盘后停止文件浏览刷新（Shown 时重启）
            _searchDebounceTimer.Stop();
            TrayAppContext.TrayIcon?.ShowBalloonTip(3000, "CloudPan",
                "仍在后台运行，双击托盘图标重新打开。", ToolTipIcon.Info);
        }
    }
}
