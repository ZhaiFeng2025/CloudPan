using System.Diagnostics;
using System.Drawing.Drawing2D;
using CloudPan.Client.Services;
using CloudPan.Shared;

namespace CloudPan.Client.UI;

/// <summary>
/// 主窗口——文件浏览主视图（T-013）。顶部一条同步状态汇总（指示灯+状态+进度+速率），
/// 主区为文件浏览（面包屑/搜索/列表-网格切换/排序/每文件状态图标），日志移入可折叠侧栏（默认折叠）。
/// WinForms 实现，包含 GDI+ 发光状态指示灯、带百分比文字的进度条、统一日志过滤及系统托盘最小化。
/// </summary>
public class MainWindow : Form
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
        Icon = CloudPanIcon.Create();
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
    }

    // ================================================================
    // 布局
    // ================================================================

    private void BuildLayout()
    {
        var baseFont = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;

        // ── 顶部状态栏（单行汇总，T-013：同步状态收敛为顶部一条） ──
        TableLayoutPanel statusTable = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 48,
            Padding = new Padding(10, 2, 10, 2),
            BackColor = CloudPanColors.BackgroundGray,
        };
        statusTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        statusTable.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        statusTable.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));

        // 左列：指示灯 + 状态文字 + 紧凑进度条 + 速率 + 汇总信息
        FlowLayoutPanel leftFlow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
        };

        _statusDot = new GlowDot { Margin = new Padding(0, 14, 8, 0) };
        _statusLabel = new Label
        {
            Text = "连接中...",
            Font = new Font(baseFont.FontFamily, CloudPanFonts.SizeBody, FontStyle.Bold),
            AutoSize = true,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0, 11, 12, 0),
        };

        _progressBar = new ProgressBarWithText
        {
            Width = 110,
            Height = 20,
            Margin = new Padding(0, 12, 8, 0),
            Visible = false,
        };

        _speedLabel = new Label
        {
            Text = "",
            AutoSize = true,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = CloudPanColors.TextMuted,
            Margin = new Padding(0, 14, 8, 0),
        };

        _statusInfo = new Label
        {
            Text = "",
            AutoSize = true,
            Font = new Font(baseFont.FontFamily, CloudPanFonts.SizeBodySmall),
            ForeColor = CloudPanColors.TextSecondary,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0, 15, 0, 0),
        };

        leftFlow.Controls.AddRange(new Control[] { _statusDot, _statusLabel, _progressBar, _speedLabel, _statusInfo });
        statusTable.Controls.Add(leftFlow, 0, 0);

        // 右列：操作按钮（触控目标 ≥ MinTouchSize=44，T-013）
        FlowLayoutPanel buttonPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
        };
        const int btnHeight = CloudPanSpacing.MinTouchSize;

        // 错误计数（在按钮左侧，点击弹出错误列表）
        _errorCountLabel = new Label
        {
            Text = "",
            AutoSize = true,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = CloudPanColors.TextError,
            Font = new Font(baseFont.FontFamily, CloudPanFonts.SizeBodySmall),
            Cursor = Cursors.Hand,
            Margin = new Padding(0, 15, 4, 0),
            Visible = false,
        };
        _errorCountLabel.Click += ErrorCountLabel_Click;
        ToolTip errorTooltip = new ToolTip { ShowAlways = true };
        errorTooltip.SetToolTip(_errorCountLabel, "点击查看同步错误");

        _openFolderButton = new Button
        {
            Text = "打开文件夹",
            Width = CloudPanSpacing.ButtonWidth,
            Height = btnHeight,
            FlatStyle = FlatStyle.Flat,
            Margin = new Padding(0, 2, 0, 0),
            UseVisualStyleBackColor = true,
        };
        _openFolderButton.FlatAppearance.BorderColor = CloudPanColors.ButtonBorderGray;
        _openFolderButton.Click += OpenFolderButton_Click;

        _logToggleButton = new Button
        {
            Text = "日志",
            Width = 64,
            Height = btnHeight,
            FlatStyle = FlatStyle.Flat,
            Margin = new Padding(4, 2, 0, 0),
            UseVisualStyleBackColor = true,
        };
        _logToggleButton.FlatAppearance.BorderColor = CloudPanColors.ButtonBorderGray;
        _logToggleButton.Click += LogToggleButton_Click;

        _pauseButton = new Button
        {
            Text = "暂停",
            Width = 68,
            Height = btnHeight,
            FlatStyle = FlatStyle.Flat,
            Margin = new Padding(4, 2, 0, 0),
            UseVisualStyleBackColor = false,
            BackColor = CloudPanColors.BackgroundLight,
        };
        ToolTip tooltip = new ToolTip { ShowAlways = true };
        tooltip.SetToolTip(_pauseButton, "暂停/恢复文件同步");
        _pauseButton.FlatAppearance.BorderColor = CloudPanColors.ButtonBorderGray;
        _pauseButton.Click += PauseButton_Click;

        _conflictButton = new Button
        {
            Text = "冲突",
            Width = 68,
            Height = btnHeight,
            FlatStyle = FlatStyle.Flat,
            Margin = new Padding(4, 2, 0, 0),
            UseVisualStyleBackColor = false,
            BackColor = CloudPanColors.WarningBgLight,
            Visible = false,
        };
        _conflictButton.FlatAppearance.BorderColor = CloudPanColors.WarningOrange;
        _conflictButton.Click += ConflictButton_Click;
        ToolTip conflictTooltip = new ToolTip { ShowAlways = true };
        conflictTooltip.SetToolTip(_conflictButton, "查看未解决的冲突");

        _retryButton = new Button
        {
            Text = "重试",
            Width = 68,
            Height = btnHeight,
            FlatStyle = FlatStyle.Flat,
            Margin = new Padding(4, 2, 0, 0),
            UseVisualStyleBackColor = false,
            BackColor = CloudPanColors.ErrorBgLight,
            Visible = false,
        };
        _retryButton.FlatAppearance.BorderColor = CloudPanColors.ErrorRed;
        _retryButton.Click += RetryButton_Click;

        // LTR 顺序：错误计数 | 打开文件夹 | 日志 | 暂停 | 冲突(条件) | 重试(条件)
        buttonPanel.Controls.Add(_errorCountLabel);
        buttonPanel.Controls.Add(_openFolderButton);
        buttonPanel.Controls.Add(_logToggleButton);
        buttonPanel.Controls.Add(_pauseButton);
        buttonPanel.Controls.Add(_conflictButton);
        buttonPanel.Controls.Add(_retryButton);
        statusTable.Controls.Add(buttonPanel, 1, 0);

        // ── 主区：文件浏览主视图（左）+ 日志侧栏（右，可折叠） ──
        _fileBrowser = new FileBrowserView { Dock = DockStyle.Fill };

        // 日志侧栏
        Panel logSidebar = new Panel { Dock = DockStyle.Fill, BackColor = CloudPanColors.BackgroundLight };
        Panel logHeader = new Panel { Dock = DockStyle.Top, Height = 36, BackColor = CloudPanColors.BackgroundGray };
        Label logTitle = new Label
        {
            Text = "  最近活动",
            Dock = DockStyle.Left,
            Font = new Font(baseFont.FontFamily, CloudPanFonts.SizeBody, FontStyle.Bold),
            ForeColor = CloudPanColors.TextMuted,
            Height = 36,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoSize = true,
        };

        _logFilterComboBox = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Dock = DockStyle.Right,
            Width = 130,
            Font = new Font(baseFont.FontFamily, CloudPanFonts.SizeCaption),
            Margin = new Padding(0, 6, 8, 0),
        };
        _logFilterComboBox.Items.AddRange(new object[] { "全部", "仅文件操作", "仅错误" });
        _logFilterComboBox.SelectedIndex = 0;
        _logFilterComboBox.SelectedIndexChanged += LogFilter_SelectedIndexChanged;
        logHeader.Controls.Add(logTitle);
        logHeader.Controls.Add(_logFilterComboBox);

        _logList = new ListBox
        {
            Dock = DockStyle.Fill,
            Font = new Font(CloudPanFonts.FontFamilyMono, 9f, FontStyle.Regular, GraphicsUnit.Point),
            IntegralHeight = false,
            BackColor = CloudPanColors.BackgroundLight,
            BorderStyle = BorderStyle.None,
        };
        var ver = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version;
        string verStr = ver != null ? $"v{ver.Major}.{ver.Minor}.{ver.Build}" : "(开发版本)";
        _logList.Items.Add($"CloudPan 客户端 {verStr}");
        _logList.Items.Add("正在连接服务端，首次连接可能需要数秒...");

        logSidebar.Controls.Add(_logList);
        logSidebar.Controls.Add(logHeader);

        _splitter = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            Panel1MinSize = 360,
            Panel2MinSize = 220,
            FixedPanel = FixedPanel.Panel2,
            BackColor = CloudPanColors.BorderLight,
        };
        _splitter.Panel1.Controls.Add(_fileBrowser);
        _splitter.Panel2.Controls.Add(logSidebar);
        _splitter.Panel2Collapsed = true; // 日志侧栏默认折叠，主视图为文件浏览

        // ── 控件入窗体（z-order：状态栏最上，分隔线次之，主区填充） ──
        Controls.Add(statusTable);    // 顶部状态栏
        Controls.Add(new Panel { Dock = DockStyle.Top, Height = 1, BackColor = CloudPanColors.BorderLight }); // 状态栏与主区分隔线
        Controls.Add(_splitter);      // 主区：文件浏览 + 日志侧栏
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
    }

    // ================================================================
    // 状态更新
    // ================================================================

    private void OnStatusChanged(string status)
    {
        if (InvokeRequired)
        {
            Invoke(() => ApplyStatus(status));
            return;
        }
        ApplyStatus(status);
    }

    /// <summary>
    /// 根据状态字符串更新顶部汇总：状态文字、指示灯颜色、上次同步时间与量化信息。
    /// </summary>
    private void ApplyStatus(string status)
    {
        _statusLabel.Text = status;

        // ── 状态→颜色映射 ──
        var color = status switch
        {
            string s when s.Contains("错误") || s.Contains("异常") || s.Contains("失败")
                => CloudPanColors.ErrorRed,
            string s when s.Contains("暂停")
                => CloudPanColors.WarningOrange,
            string s when s.Contains("连接") || s.Contains("等待")
                => CloudPanColors.TextMuted,
            string s when s.Contains("就绪") || s.Contains("运行中")
                => CloudPanColors.SuccessGreen,
            _ => CloudPanColors.AccentBlue
        };

        if (_statusDot.BackColor != color)
        {
            _statusDot.BackColor = color;
            _statusDot.Invalidate();
        }

        // ── 上次同步时间（供顶部汇总展示） ──
        if (status.Contains("就绪") || status.Contains("运行中"))
        {
            _lastSyncTime = DateTime.Now;
        }

        // ── 量化的状态文字 ──
        UpdateStatusInfoText(status);

        // ── 错误时显示重试按钮 ──
        bool hasError = status.Contains("错误") || status.Contains("异常") || status.Contains("失败");
        _retryButton.Visible = hasError;
    }

    /// <summary>判断状态是否表示正在同步中。</summary>
    private static bool IsActiveStatus(string status)
    {
        return status.Contains("同步") || status.Contains("上传") || status.Contains("下载");
    }

    /// <summary>更新顶部汇总的量化状态文字。同步中信息由 ApplyQueueProgress 通过 SyncStatus 对象设置，此处只处理空闲状态。</summary>
    private void UpdateStatusInfoText(string status)
    {
        // 同步中时保留 ApplyQueueProgress 设置的详细进度信息，不覆盖
        if (IsActiveStatus(status))
        {
            return;
        }

        if (_lastFileTotal > 0 || _lastSyncTime.HasValue)
        {
            // 空闲时显示文件计数和上次同步时间
            string fileInfo = _lastFileTotal > 0
                ? $"已同步 {_lastFileCompleted}/{_lastFileTotal} 文件"
                : "";
            string timeInfo = _lastSyncTime.HasValue
                ? $"上次同步: {_lastSyncTime.Value:HH:mm}"
                : "";
            _statusInfo.Text = string.Join(" · ", new[] { fileInfo, timeInfo }.Where(s => !string.IsNullOrEmpty(s)));
        }
        else
        {
            _statusInfo.Text = "";
        }
    }

    // ================================================================
    // 进度更新（字节级 SyncStatus）
    // ================================================================

    private void OnQueueProgressChanged(SyncStatus syncStatus)
    {
        if (InvokeRequired)
        {
            Invoke(() => ApplyQueueProgress(syncStatus));
            return;
        }
        ApplyQueueProgress(syncStatus);
    }

    /// <summary>
    /// 更新进度条（基于字节数，带百分比文字）、传输速率和状态量化信息。
    /// 取代原有的基于项数的进度跟踪。
    /// </summary>
    private void ApplyQueueProgress(SyncStatus status)
    {
        // 更新缓存的跟踪值
        _lastTotalBytes = status.TotalBytes;
        _lastBytesTransferred = status.BytesTransferred;
        _lastCurrentFile = status.CurrentFile;
        _lastFileTotal = status.TotalFiles;
        _lastFileCompleted = status.CompletedFiles;

        // ── 进度条（归一化到 0-10000 范围，避免 >2GB 溢出） ──
        const int progressMax = 10000;
        _progressBar.Maximum = progressMax;
        if (status.TotalBytes > 0)
        {
            _progressBar.Visible = true;
            double ratio = (double)status.BytesTransferred / Math.Max(status.TotalBytes, 1);
            _progressBar.Value = (int)(ratio * progressMax);
            _progressBar.PercentageText = $"{ratio * 100:F0}%";
        }
        else if (status.TotalFiles > 0)
        {
            // 无字节信息时回退到文件级进度
            _progressBar.Visible = true;
            double ratio = (double)status.CompletedFiles / Math.Max(status.TotalFiles, 1);
            _progressBar.Value = (int)(ratio * progressMax);
            _progressBar.PercentageText = $"{ratio * 100:F0}%";
        }
        else
        {
            _progressBar.Visible = false;
            _progressBar.PercentageText = "";
        }

        // ── 传输速率 ──
        if (status.SpeedBytesPerSec > 0)
        {
            _speedLabel.Text = $"{FormatDataRate(status.SpeedBytesPerSec)}/s";
        }
        else
        {
            _speedLabel.Text = "";
        }

        // ── 状态栏第二行（量化信息） ──
        if (!string.IsNullOrEmpty(status.CurrentFile))
        {
            // 字节级百分比
            string pct = status.TotalBytes > 0
                ? $"{(double)status.BytesTransferred / Math.Max(status.TotalBytes, 1) * 100:F0}%"
                : status.TotalFiles > 0
                    ? $"{(double)status.CompletedFiles / Math.Max(status.TotalFiles, 1) * 100:F0}%"
                    : "";
            // 速率可能尚未计算出来，避免显示 ", 45%" 这种前导逗号
            string ratePart = !string.IsNullOrEmpty(_speedLabel.Text)
                ? $"{_speedLabel.Text}, "
                : "";
            _statusInfo.Text = $"正在同步: {status.CurrentFile} ({ratePart}{pct})";
        }
        else if (status.TotalFiles > 0)
        {
            string timeInfo = _lastSyncTime.HasValue
                ? $"上次同步: {_lastSyncTime.Value:HH:mm}"
                : "";
            _statusInfo.Text = $"已同步 {status.CompletedFiles}/{status.TotalFiles} 文件 · {timeInfo}";
        }

    }

    // ================================================================
    // 嵌入式错误面板（状态栏错误计数 + 弹出列表）
    // ================================================================

    private void OnConflictDetected(ConflictInfo conflict)
    {
        if (InvokeRequired)
        {
            Invoke(() => OnConflictDetected(conflict));
            return;
        }

        _conflicts.Add((conflict, DateTime.Now));
        UpdateConflictBadge();
        AddLog($"冲突: {Path.GetFileName(conflict.RelativePath)} — 本地和远程同时变更");
        ShowConflictResolution(conflict);
    }

    private void OnErrorOccurred(string filePath, string errorMessage, SyncOperation operation)
    {
        if (InvokeRequired)
        {
            Invoke(() => OnErrorOccurred(filePath, errorMessage, operation));
            return;
        }

        // 去重：同一文件同一错误不重复添加
        if (_errors.Any(e => e.FilePath == filePath && e.Message == errorMessage))
        {
            return;
        }

        SyncErrorInfo errorInfo = new SyncErrorInfo
        {
            FilePath = filePath,
            Message = errorMessage,
            Timestamp = DateTime.Now,
            Operation = operation
        };

        _errors.Add(errorInfo);
        UpdateErrorBadge();
        AddLog($"错误: {filePath} — {errorMessage}");
    }

    /// <summary>更新状态栏错误计数标签的显示。</summary>
    private void UpdateErrorBadge()
    {
        int count = _errors.Count;
        if (count == 0)
        {
            _errorCountLabel.Visible = false;
            return;
        }

        _errorCountLabel.Text = $"❌ {count}";
        _errorCountLabel.Visible = true;
    }

    // ===== 具名事件处理器（CP301：避免匿名 lambda 订阅无法退订） =====

    private void ErrorCountLabel_Click(object? sender, EventArgs e) => ShowErrorPopup();

    private void OpenFolderButton_Click(object? sender, EventArgs e) => OpenSyncFolder();

    private void PauseButton_Click(object? sender, EventArgs e) => TogglePause();

    private void ConflictButton_Click(object? sender, EventArgs e) => ShowConflictList();

    private void RetryButton_Click(object? sender, EventArgs e) => RetrySync();

    private void LogToggleButton_Click(object? sender, EventArgs e) => ToggleLogSidebar();

    private void LogFilter_SelectedIndexChanged(object? sender, EventArgs e) => ApplyLogFilter();

    /// <summary>点击错误计数标签时弹出错误列表对话框。</summary>
    private void ShowErrorPopup()
    {
        if (_errors.Count == 0)
        {
            return;
        }

        Form dialog = new Form
        {
            Text = $"同步错误 ({_errors.Count})",
            Size = new Size(580, 380),
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
        };

        ListBox listBox = new ListBox
        {
            Dock = DockStyle.Fill,
            IntegralHeight = false,
            Font = new Font(CloudPanFonts.FontFamilyMono, 9f),
            Padding = new Padding(8),
        };

        foreach (var err in _errors)
        {
            string fileName = Path.GetFileName(err.FilePath);
            listBox.Items.Add($"[{err.Timestamp:HH:mm:ss}] {fileName} — {err.Message}");
        }

        // 右键菜单：单条重试/忽略（本地函数捕获局部状态，同时满足 CP301 具名订阅）
        ContextMenuStrip errorCms = new ContextMenuStrip();
        async void OnRetryItemClick(object? s, EventArgs e)
        {
            int idx = listBox.SelectedIndex;
            if (idx >= 0 && idx < _errors.Count)
            {
                var err = _errors[idx];
                await RetrySingleErrorAsync(err);
                listBox.Items.RemoveAt(idx);
                UpdateErrorBadge();
                if (_errors.Count == 0)
                {
                    dialog.Close();
                }
            }
        }
        void OnIgnoreItemClick(object? s, EventArgs e)
        {
            int idx = listBox.SelectedIndex;
            if (idx >= 0 && idx < _errors.Count)
            {
                _errors.RemoveAt(idx);
                listBox.Items.RemoveAt(idx);
                UpdateErrorBadge();
                if (_errors.Count == 0)
                {
                    dialog.Close();
                }
            }
        }
        errorCms.Items.Add("重试该项", null, OnRetryItemClick);
        errorCms.Items.Add("忽略该项", null, OnIgnoreItemClick);
        void OnListBoxMouseDown(object? s, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right)
            {
                int idx = listBox.IndexFromPoint(e.Location);
                if (idx >= 0)
                {
                    listBox.SelectedIndex = idx;
                    errorCms.Show(listBox, e.Location);
                }
            }
        }
        listBox.MouseDown += OnListBoxMouseDown;

        // 底部按钮栏
        FlowLayoutPanel btnPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 40,
            Padding = new Padding(8),
        };

        Button closeBtn = new Button { Text = "关闭", Width = 80, Height = 28, FlatStyle = FlatStyle.Flat };
        closeBtn.FlatAppearance.BorderColor = CloudPanColors.ButtonBorderGray;
        void OnCloseBtnClick(object? s, EventArgs e) => dialog.Close();
        closeBtn.Click += OnCloseBtnClick;

        Button retryAllBtn = new Button
        {
            Text = "全部重试",
            Width = 100,
            Height = 28,
            FlatStyle = FlatStyle.Flat,
            Margin = new Padding(0, 0, 8, 0),
            UseVisualStyleBackColor = false,
            BackColor = CloudPanColors.ErrorBgLight,
        };
        retryAllBtn.FlatAppearance.BorderColor = CloudPanColors.ErrorRed;
        async void OnRetryAllClick(object? s, EventArgs e)
        {
            await RetryAllErrorsAsync();
            dialog.Close();
        }
        retryAllBtn.Click += OnRetryAllClick;

        Button dismissAllBtn = new Button
        {
            Text = "忽略全部",
            Width = 80,
            Height = 28,
            FlatStyle = FlatStyle.Flat,
            Margin = new Padding(0, 0, 4, 0),
        };
        dismissAllBtn.FlatAppearance.BorderColor = CloudPanColors.ButtonBorderGray;
        void OnDismissAllClick(object? s, EventArgs e)
        {
            _errors.Clear();
            UpdateErrorBadge();
            dialog.Close();
        }
        dismissAllBtn.Click += OnDismissAllClick;

        btnPanel.Controls.Add(closeBtn);
        btnPanel.Controls.Add(retryAllBtn);
        btnPanel.Controls.Add(dismissAllBtn);

        dialog.Controls.Add(listBox);
        dialog.Controls.Add(btnPanel);
        dialog.ShowDialog(this);
    }

    /// <summary>异步重试所有错误条目。</summary>
    private async Task RetryAllErrorsAsync()
    {
        List<SyncErrorInfo> copy = _errors.ToList();
        foreach (var err in copy)
        {
            try
            {
                switch (err.Operation)
                {
                    case SyncOperation.Upload:
                        await _engine.EnqueueLocalChangeAsync(err.FilePath, SyncOperation.Upload);
                        break;
                    case SyncOperation.Download:
                        await _engine.DownloadPathAsync(err.FilePath);
                        break;
                    case SyncOperation.Delete:
                        await _engine.EnqueueLocalChangeAsync(err.FilePath, SyncOperation.Delete);
                        break;
                    case SyncOperation.Rename:
                        await _engine.EnqueueLocalChangeAsync(err.FilePath, SyncOperation.Rename);
                        break;
                }
                _errors.Remove(err);
            }
            catch (Exception ex)
            {
                AddLog($"重试失败: {err.FilePath} — {ex.Message}");
            }
        }
        UpdateErrorBadge();
    }

    /// <summary>异步重试单个错误条目。</summary>
    private async Task RetrySingleErrorAsync(SyncErrorInfo err)
    {
        try
        {
            switch (err.Operation)
            {
                case SyncOperation.Upload:
                    await _engine.EnqueueLocalChangeAsync(err.FilePath, SyncOperation.Upload);
                    break;
                case SyncOperation.Download:
                    await _engine.DownloadPathAsync(err.FilePath);
                    break;
                case SyncOperation.Delete:
                    await _engine.EnqueueLocalChangeAsync(err.FilePath, SyncOperation.Delete);
                    break;
                case SyncOperation.Rename:
                    await _engine.EnqueueLocalChangeAsync(err.FilePath, SyncOperation.Rename);
                    break;
            }
            _errors.Remove(err);
            AddLog($"重试成功: {err.FilePath}");
        }
        catch (Exception ex)
        {
            AddLog($"重试失败: {err.FilePath} — {ex.Message}");
        }
    }

    /// <summary>忽略单个错误条目——仅从错误列表中移除。</summary>
    private void DismissError(SyncErrorInfo error)
    {
        _errors.Remove(error);
        UpdateErrorBadge();
        AddLog($"已忽略错误: {error.FilePath}");
    }

    // ================================================================
    // 日志过滤（统一列表 + 过滤下拉框）
    // ================================================================

    /// <summary>线程安全地向日志添加消息。</summary>
    public void AddLog(string message)
    {
        if (InvokeRequired)
        {
            Invoke(() => AddLogCore(message));
            return;
        }
        AddLogCore(message);
    }

    private void AddLogCore(string message)
    {
        string formatted = FormatLogMessage(message);
        _allLogEntries.Add(formatted);

        // 根据当前过滤模式决定是否显示
        int filter = _logFilterComboBox.SelectedIndex;
        bool shouldShow = filter switch
        {
            0 => true,
            1 => IsFileOperationEntry(formatted),
            2 => IsErrorEntry(formatted),
            _ => true
        };

        if (shouldShow)
        {
            _logList.Items.Add(formatted);
            while (_logList.Items.Count > MaxLogItems + 1) // +1 保留表头
            {
                _logList.Items.RemoveAt(1); // 保留第一条表头
            }

            if (_logList.Items.Count > 0)
            {
                _logList.TopIndex = _logList.Items.Count - 1;
            }
        }
    }

    /// <summary>格式化日志消息——添加图标前缀、时间戳、路径简化和截断。</summary>
    private static string FormatLogMessage(string message)
    {
        string icon = message switch
        {
            string s when s.Contains("上传完成") || s.Contains("下载完成") => "✅ ",
            string s when s.Contains("失败") || s.Contains("异常") => "❌ ",
            string s when s.Contains("冲突") => "⚠️ ",
            string s when s.Contains("上传") || s.Contains("下载") || s.Contains("同步") => "🔄 ",
            string s when s.Contains("删除") => "🗑️ ",
            string s when s.Contains("重命名") => "✏️ ",
            _ => "📋 "
        };

        // 提取路径简化为文件名
        string display = message;
        if (message.Contains('/'))
        {
            string[] parts = message.Split(' ');
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i].StartsWith("/"))
                {
                    parts[i] = Path.GetFileName(parts[i]);
                    display = string.Join(" ", parts).Replace("  ", " ");
                }
            }
        }
        if (display.Length > 80)
        {
            display = display[..77] + "...";
        }

        return $"{icon}[{DateTime.Now:HH:mm:ss}] {display}";
    }

    /// <summary>判断日志条目是否为文件操作类（根据图标前缀）。</summary>
    private static bool IsFileOperationEntry(string entry)
    {
        return entry.Contains("✅ ") || entry.Contains("❌ ") || entry.Contains("🔄 ") ||
               entry.Contains("🗑️ ") || entry.Contains("✏️ ");
    }

    /// <summary>判断日志条目是否为错误类。</summary>
    private static bool IsErrorEntry(string entry)
    {
        return entry.Contains("❌ ") || entry.Contains("失败") || entry.Contains("错误") || entry.Contains("异常");
    }

    /// <summary>过滤下拉框变更时重新填充日志列表。</summary>
    private void ApplyLogFilter()
    {
        _logList.Items.Clear();

        // 重新添加表头
        var ver = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version;
        string verStr = ver != null ? $"v{ver.Major}.{ver.Minor}.{ver.Build}" : "(开发版本)";
        _logList.Items.Add($"CloudPan 客户端 {verStr}");

        int filter = _logFilterComboBox.SelectedIndex;

        // 从最新的条目开始反向填充，保留最后 MaxLogItems 条匹配的条目
        int count = 0;
        for (int i = _allLogEntries.Count - 1; i >= 0 && count < MaxLogItems; i--)
        {
            string entry = _allLogEntries[i];
            bool show = filter switch
            {
                0 => true,
                1 => IsFileOperationEntry(entry),
                2 => IsErrorEntry(entry),
                _ => true
            };

            if (show)
            {
                _logList.Items.Insert(1, entry); // 插入到表头之后
                count++;
            }
        }

        if (_logList.Items.Count > 0)
        {
            _logList.TopIndex = _logList.Items.Count - 1;
        }
    }

    // ================================================================
    // 文件浏览主视图（T-013）
    // ================================================================

    /// <summary>窗口首次显示/再次显示时启动文件浏览定时刷新并立即加载一次。</summary>
    private void OnShown(object? sender, EventArgs e)
    {
        _browserRefreshTimer.Start();
        BrowserRefreshTimer_Tick(sender, e);
    }

    /// <summary>定时刷新文件浏览（UI 定时器回调，async void + 顶层 try-catch 符合 CLAUDE.md 7.2）。</summary>
    private async void BrowserRefreshTimer_Tick(object? sender, EventArgs e)
    {
        if (_browserRefreshBusy)
        {
            return; // 上一次刷新仍在进行，跳过本次定时触发（防重入）
        }

        _browserRefreshBusy = true;
        try
        {
            await LoadBrowserAsync();
        }
        catch (Exception ex)
        {
            // 刷新失败不影响主界面，下次定时器触发自动重试
            System.Diagnostics.Debug.WriteLine($"刷新文件浏览失败: {ex.Message}");
        }
        finally
        {
            _browserRefreshBusy = false;
        }
    }

    /// <summary>搜索防抖定时器 Tick：停止输入后重载当前浏览目录（保持搜索关键字）。UI 定时器回调，async void + 顶层 try-catch 符合 CLAUDE.md 7.2。</summary>
    private async void SearchDebounceTimer_Tick(object? sender, EventArgs e)
    {
        _searchDebounceTimer.Stop();
        try
        {
            await LoadBrowserAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"搜索刷新失败: {ex.Message}");
        }
    }

    /// <summary>从 SyncEngine 查询文件浏览数据并渲染（数据查询在 Client.Core，UI 只渲染）。</summary>
    private async Task LoadBrowserAsync()
    {
        IReadOnlyList<FileBrowseItem> items = await _engine.GetFileBrowserAsync(_currentPath, _searchText);
        if (InvokeRequired)
        {
            Invoke(() => ApplyBrowser(items));
            return;
        }
        ApplyBrowser(items);
    }

    /// <summary>将文件浏览数据交给文件浏览视图渲染。</summary>
    private void ApplyBrowser(IReadOnlyList<FileBrowseItem> items)
    {
        _fileBrowser.ShowItems(_currentPath, items, _searchText);
    }

    /// <summary>导航到指定目录（清空搜索时经 SearchTextChanged 重载，否则直接重载）。UI 事件上下文，async void + 顶层 try-catch 符合 CLAUDE.md 7.2。</summary>
    private async void NavigateTo(string path)
    {
        _currentPath = path;
        try
        {
            if (!string.IsNullOrEmpty(_searchText))
            {
                _fileBrowser.ClearSearch(); // 触发 SearchTextChanged → 以新路径、空搜索重载
            }
            else
            {
                await LoadBrowserAsync();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"导航到 {path} 失败: {ex.Message}");
        }
    }

    /// <summary>双击子目录 / 点击面包屑段：进入目录。</summary>
    private void FileBrowser_DirectoryActivated(string path) => NavigateTo(path);

    /// <summary>双击文件：尝试用系统默认程序打开本地副本。</summary>
    private void FileBrowser_FileActivated(string path) => OpenFile(path);

    /// <summary>点击「上一级」：进入父目录。</summary>
    private void FileBrowser_UpRequested() => NavigateTo(GetParentPath(_currentPath));

    /// <summary>搜索框内容变化：记录搜索关键字并启动防抖重载。</summary>
    private void FileBrowser_SearchTextChanged(string text)
    {
        _searchText = string.IsNullOrWhiteSpace(text) ? null : text;
        _searchDebounceTimer.Stop();
        _searchDebounceTimer.Start();
    }

    /// <summary>计算目录的父目录路径（"/" 的父目录为 "/"）。</summary>
    private static string GetParentPath(string path)
    {
        string p = path.TrimEnd('/');
        if (p.Length == 0)
        {
            return "/";
        }

        int idx = p.LastIndexOf('/');
        return idx <= 0 ? "/" : p[..idx];
    }

    /// <summary>打开文件浏览视图中的文件：本地存在则系统打开，CloudOnly 未下载则提示。</summary>
    private void OpenFile(string relativePath)
    {
        string localPath = System.IO.Path.Combine(
            Program.SyncRoot, relativePath.TrimStart('/').Replace('/', System.IO.Path.DirectorySeparatorChar));
        if (System.IO.File.Exists(localPath))
        {
            try
            {
                Process.Start(new ProcessStartInfo(localPath) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                AddLog($"打开文件失败: {relativePath} — {ex.Message}");
            }
        }
        else
        {
            AddLog($"该文件仅在云端（CloudOnly），未下载到本地，暂无法打开: {relativePath}");
        }
    }

    /// <summary>将 FileBrowseItem 映射为（图标, 颜色）双通道。错误/冲突覆盖优先级最高（瞬时状态优先可见），其余按 FileState 枚举。</summary>
    private (string Icon, Color Color) ResolveBrowseState(FileBrowseItem item)
    {
        if (_errors.Any(e => string.Equals(e.FilePath, item.Path, StringComparison.OrdinalIgnoreCase)))
        {
            return ("✗", CloudPanColors.ErrorRed);
        }

        if (_conflicts.Any(c => string.Equals(c.Info.RelativePath, item.Path, StringComparison.OrdinalIgnoreCase)))
        {
            return ("!", CloudPanColors.WarningOrange);
        }

        return item.State switch
        {
            (int)FileState.Synced => ("✓", CloudPanColors.SuccessGreen),
            (int)FileState.Uploading => ("↻", CloudPanColors.AccentBlue),
            (int)FileState.Downloading => ("↻", CloudPanColors.AccentBlue),
            (int)FileState.Modified => ("↻", CloudPanColors.AccentBlue),
            (int)FileState.CloudOnly => ("☁", CloudPanColors.TextMuted),
            (int)FileState.Conflict => ("!", CloudPanColors.WarningOrange),
            _ => ("✓", CloudPanColors.SuccessGreen)
        };
    }

    // ================================================================
    // 操作
    // ================================================================

    private void RetrySync()
    {
        _engine.SetPaused(false);
        _retryButton.Visible = false;
        AddLog("手动触发重试，同步将在数秒内恢复...");
    }

    private void TogglePause()
    {
        _paused = !_paused;
        _engine.SetPaused(_paused);
        _pauseButton.Text = _paused ? "继续" : "暂停";
        _pauseButton.ForeColor = _paused ? CloudPanColors.ErrorRed : CloudPanColors.TextSecondary;
        _pauseButton.BackColor = _paused ? CloudPanColors.WarningBgLight : CloudPanColors.BackgroundLight;
        AddLog(_paused ? "同步已暂停" : "同步已恢复");
    }

    /// <summary>切换日志侧栏的展开/折叠（T-013：日志不再占主界面，默认折叠）。</summary>
    private void ToggleLogSidebar()
    {
        if (_splitter.Panel2Collapsed)
        {
            _splitter.Panel2Collapsed = false;
            _splitter.SplitterDistance = Math.Max(_splitter.Width - _logSidebarWidth, _splitter.Panel1MinSize);
            _logToggleButton.Text = "收起日志";
        }
        else
        {
            _logSidebarWidth = Math.Max(_splitter.Width - _splitter.SplitterDistance, _splitter.Panel2MinSize);
            _splitter.Panel2Collapsed = true;
            _logToggleButton.Text = "日志";
        }
    }

    private void OpenSyncFolder()
    {
        try
        {
            Process.Start("explorer.exe", Program.SyncRoot);
        }
        catch (Exception ex)
        {
            string msg = $"无法打开同步文件夹:\n{Program.SyncRoot}\n\n原因: {ex.Message}";
            MessageBox.Show(msg, "CloudPan", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // ================================================================
    // 冲突管理
    // ================================================================

    public void ShowConflictWarning(string path)
    {
        if (InvokeRequired)
        {
            Invoke(() => ShowConflictWarning(path));
            return;
        }

        // 收集冲突信息
        string localPath = System.IO.Path.Combine(Program.SyncRoot, path.TrimStart('/'));
        DateTime localModified = DateTime.MinValue;
        long localSize = 0;
        try
        {
            if (File.Exists(localPath))
            {
                FileInfo fi = new FileInfo(localPath);
                localModified = fi.LastWriteTime;
                localSize = fi.Length;
            }
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"无法读取本地文件信息: {ex.Message}"); }

        // 从本地缓存快照获取最近已知的远程信息
        long? remoteSize = null;
        try
        {
            string dbPath = System.IO.Path.Combine(Program.SyncRoot, ".cloudpan", "client.db");
            if (File.Exists(dbPath))
            {
                using Models.ClientDbContext db = new Models.ClientDbContext(dbPath);
                var snapshot = db.RemoteSnapshots.Find(path);
                if (snapshot != null)
                {
                    remoteSize = snapshot.Size;
                }
            }
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"无法读取远程快照: {ex.Message}"); }

        ConflictInfo conflict = new ConflictInfo(
            RelativePath: path,
            LocalPath: localPath,
            LocalModifiedTime: localModified,
            RemoteModifiedTime: null,
            LocalFileSize: localSize,
            RemoteFileSize: remoteSize,
            RemoteHash: null
        );

        _conflicts.Add((conflict, DateTime.Now));
        UpdateConflictBadge();
        ShowConflictResolution(conflict);
    }

    /// <summary>显示冲突解决对话框——版本对比区域带颜色边框（本地蓝、远程绿）。</summary>
    private void ShowConflictResolution(ConflictInfo conflict)
    {
        string fileName = System.IO.Path.GetFileName(conflict.RelativePath);
        string localTime = conflict.LocalModifiedTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? "未知";
        string localSizeStr = FormatFileSize(conflict.LocalFileSize);
        string remoteTime = conflict.RemoteModifiedTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? "未知（服务端已变更）";
        string remoteSizeStr = conflict.RemoteFileSize.HasValue ? FormatFileSize(conflict.RemoteFileSize.Value) : "未知";

        Form dialog = new Form
        {
            Text = $"文件冲突 — {fileName}",
            Size = new Size(560, 300),
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
        };

        TableLayoutPanel layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16),
            RowCount = 5,
            ColumnCount = 1,
        };

        // 标题
        Label titleLabel = new Label
        {
            Text = $"\"{fileName}\" 在本地和远程同时发生了变更",
            Font = new Font((SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont).FontFamily, 10f, FontStyle.Bold),
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 8),
        };
        layout.Controls.Add(titleLabel, 0, 0);

        // 本地版本（蓝色左边框 + 浅蓝背景）
        Panel localPanel = new Panel
        {
            Height = 28,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 2, 0, 2),
            Padding = new Padding(8, 0, 0, 0),
            BackColor = CloudPanColors.InfoBgLight, // AliceBlue 浅蓝
        };
        void OnLocalPanelPaint(object? s, PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using Pen pen = new Pen(CloudPanColors.AccentBlue, 3);
            e.Graphics.DrawLine(pen, 1, 2, 1, localPanel.Height - 4);
        }
        localPanel.Paint += OnLocalPanelPaint;
        localPanel.Controls.Add(new Label
        {
            Text = $"本地版本   修改时间: {localTime}    大小: {localSizeStr}",
            AutoSize = true,
            Font = new Font((SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont).FontFamily, 9f),
            Location = new Point(10, 5),
        });
        layout.Controls.Add(localPanel, 0, 1);

        // 远程版本（绿色左边框 + 浅绿背景）
        Panel remotePanel = new Panel
        {
            Height = 28,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 2, 0, 2),
            Padding = new Padding(8, 0, 0, 0),
            BackColor = CloudPanColors.SuccessBgLight, // Honeydew 浅绿
        };
        void OnRemotePanelPaint(object? s, PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using Pen pen = new Pen(CloudPanColors.SuccessGreen, 3);
            e.Graphics.DrawLine(pen, 1, 2, 1, remotePanel.Height - 4);
        }
        remotePanel.Paint += OnRemotePanelPaint;
        remotePanel.Controls.Add(new Label
        {
            Text = $"远程版本   修改时间: {remoteTime}    大小: {remoteSizeStr}",
            AutoSize = true,
            Font = new Font((SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont).FontFamily, 9f),
            Location = new Point(10, 5),
        });
        layout.Controls.Add(remotePanel, 0, 2);

        // 提示文字
        layout.Controls.Add(new Label
        {
            Text = "请选择处理方式:",
            AutoSize = true,
            Margin = new Padding(0, 8, 0, 0),
        }, 0, 3);

        // 按钮面板（LTR 顺序：保留本地 | 保留远程 | 保留两者）
        FlowLayoutPanel buttonPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(0, 8, 0, 0),
        };

        Button btnLocal = new Button
        {
            Text = "保留本地",
            Width = 100,
            Height = 30,
            FlatStyle = FlatStyle.Flat,
        };
        btnLocal.FlatAppearance.BorderColor = CloudPanColors.AccentBlue;
        void OnKeepLocalClick(object? s, EventArgs e)
        {
            dialog.Close();
            ResolveConflict(conflict, ConflictResolution.KeepLocal);
        }
        btnLocal.Click += OnKeepLocalClick;

        Button btnRemote = new Button
        {
            Text = "保留远程",
            Width = 100,
            Height = 30,
            FlatStyle = FlatStyle.Flat,
        };
        btnRemote.FlatAppearance.BorderColor = CloudPanColors.SuccessGreen;
        void OnKeepRemoteClick(object? s, EventArgs e)
        {
            dialog.Close();
            ResolveConflict(conflict, ConflictResolution.KeepRemote);
        }
        btnRemote.Click += OnKeepRemoteClick;

        Button btnBoth = new Button
        {
            Text = "保留两者",
            Width = 100,
            Height = 30,
            FlatStyle = FlatStyle.Flat,
        };
        btnBoth.FlatAppearance.BorderColor = CloudPanColors.WarningOrange;
        void OnKeepBothClick(object? s, EventArgs e)
        {
            dialog.Close();
            ResolveConflict(conflict, ConflictResolution.KeepBoth);
        }
        btnBoth.Click += OnKeepBothClick;

        buttonPanel.Controls.Add(btnLocal);
        buttonPanel.Controls.Add(btnRemote);
        buttonPanel.Controls.Add(btnBoth);

        layout.Controls.Add(buttonPanel, 0, 4);

        dialog.Controls.Add(layout);
        dialog.ShowDialog(this);
    }

    /// <summary>执行冲突解决，向 SyncEngine 发送回调，更新冲突列表。</summary>
    private void ResolveConflict(ConflictInfo conflict, ConflictResolution resolution)
    {
        _conflicts.RemoveAll(c => c.Info == conflict);
        UpdateConflictBadge();

        string fileName = System.IO.Path.GetFileName(conflict.RelativePath);
        AddLog(resolution switch
        {
            ConflictResolution.KeepLocal => $"冲突解决: 保留本地 — {fileName}",
            ConflictResolution.KeepRemote => $"冲突解决: 保留远程 — {fileName}",
            ConflictResolution.KeepBoth => $"冲突解决: 保留两者 — {fileName}",
            _ => $"冲突解决: {fileName}"
        });

        Task.Run(async () =>
        {
            try { await _engine.OnConflictResolved(conflict.RelativePath, resolution); }
            catch (Exception ex) { AddLog($"冲突解决失败: {ex.Message}"); }
        });
    }

    /// <summary>显示所有未解决冲突的列表对话框。</summary>
    private void ShowConflictList()
    {
        if (_conflicts.Count == 0)
        {
            MessageBox.Show("当前没有待解决的冲突。", "CloudPan",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Form listDialog = new Form
        {
            Text = $"未解决的冲突 ({_conflicts.Count})",
            Size = new Size(600, 400),
            StartPosition = FormStartPosition.CenterParent,
        };

        ListBox listBox = new ListBox
        {
            Dock = DockStyle.Top,
            Height = 280,
            IntegralHeight = false,
            Font = new Font(CloudPanFonts.FontFamilyMono, 9f),
        };

        for (int i = 0; i < _conflicts.Count; i++)
        {
            var (info, detectedAt) = _conflicts[i];
            string name = System.IO.Path.GetFileName(info.RelativePath);
            string localTime = (info.LocalModifiedTime ?? DateTime.MinValue).ToString("HH:mm:ss");
            string localSize = FormatFileSize(info.LocalFileSize);
            listBox.Items.Add($"[{i + 1}] {name}  本地: {localTime} / {localSize}  检测于: {detectedAt:HH:mm:ss}");
        }

        Button resolveBtn = new Button
        {
            Text = "解决选中冲突",
            Dock = DockStyle.Top,
            Height = 36,
            FlatStyle = FlatStyle.Flat,
        };
        resolveBtn.FlatAppearance.BorderColor = CloudPanColors.ButtonBorderGray;
        void OnResolveConflictClick(object? s, EventArgs e)
        {
            if (listBox.SelectedIndex >= 0 && listBox.SelectedIndex < _conflicts.Count)
            {
                ShowConflictResolution(_conflicts[listBox.SelectedIndex].Info);
                // 刷新列表
                listBox.Items.Clear();
                for (int i = 0; i < _conflicts.Count; i++)
                {
                    var (info, detectedAt) = _conflicts[i];
                    string name = System.IO.Path.GetFileName(info.RelativePath);
                    string localTime = (info.LocalModifiedTime ?? DateTime.MinValue).ToString("HH:mm:ss");
                    string localSize = FormatFileSize(info.LocalFileSize);
                    listBox.Items.Add($"[{i + 1}] {name}  本地: {localTime} / {localSize}  检测于: {detectedAt:HH:mm:ss}");
                }
                if (_conflicts.Count == 0)
                {
                    listDialog.Close();
                }
            }
        }
        resolveBtn.Click += OnResolveConflictClick;

        Button closeBtn = new Button
        {
            Text = "关闭",
            Dock = DockStyle.Top,
            Height = 36,
            FlatStyle = FlatStyle.Flat,
        };
        closeBtn.FlatAppearance.BorderColor = CloudPanColors.ButtonBorderGray;
        void OnListCloseClick(object? s, EventArgs e) => listDialog.Close();
        closeBtn.Click += OnListCloseClick;

        listDialog.Controls.Add(closeBtn);
        listDialog.Controls.Add(resolveBtn);
        listDialog.Controls.Add(listBox);
        listDialog.ShowDialog(this);
    }

    /// <summary>更新冲突按钮的可见性和计数文本。</summary>
    private void UpdateConflictBadge()
    {
        int count = _conflicts.Count;
        _conflictButton.Text = count > 0 ? $"冲突 ({count})" : "冲突";
        _conflictButton.Visible = count > 0;
    }

    // ================================================================
    // 格式化工具
    // ================================================================

    /// <summary>格式化文件大小为人类可读形式（B/KB/MB/GB）。</summary>
    private static string FormatFileSize(long bytes)
    {
        return bytes switch
        {
            < 1024 => $"{bytes} B",
            < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
            < 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024.0):F1} MB",
            _ => $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB"
        };
    }

    /// <summary>格式化数据传输速率（字节/秒 → "12.3 MB" 形式，小于 1MB 时显示 KB）。</summary>
    private static string FormatDataRate(double bytesPerSecond)
    {
        return bytesPerSecond switch
        {
            < 1024 => $"{bytesPerSecond:F0} B",
            < 1024 * 1024 => $"{bytesPerSecond / 1024.0:F1} KB",
            < 1024 * 1024 * 1024 => $"{bytesPerSecond / (1024.0 * 1024.0):F1} MB",
            _ => $"{bytesPerSecond / (1024.0 * 1024.0 * 1024.0):F2} GB"
        };
    }

    // ================================================================
    // 关闭行为：隐藏到托盘 + 气泡提示
    // ================================================================

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            var settings = Services.SettingsStore.Load();
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

    // ================================================================
    // 自定义控件
    // ================================================================

    /// <summary>GDI+ 绘制的发光状态指示灯。替换 Region 裁剪方案，绘制带发光效果和镜面高光的圆形。</summary>
    private class GlowDot : Panel
    {
        public GlowDot()
        {
            Size = new Size(16, 16);
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.Clear(Parent?.BackColor ?? Color.Transparent);

            float cx = Width / 2f;
            float cy = Height / 2f;
            const float radius = 5f;

            // 外层发光（使用 PathGradientBrush 实现径向渐变发光）
            using (GraphicsPath glowPath = new GraphicsPath())
            {
                float glowR = radius + 4f;
                glowPath.AddEllipse(cx - glowR, cy - glowR, glowR * 2, glowR * 2);
                using PathGradientBrush glowBrush = new PathGradientBrush(glowPath)
                {
                    CenterColor = Color.FromArgb(100, BackColor),
                    SurroundColors = new[] { Color.Transparent }
                };
                e.Graphics.FillEllipse(glowBrush, cx - glowR - 1, cy - glowR - 1,
                                       (glowR + 1) * 2, (glowR + 1) * 2);
            }

            // 实心圆
            using (SolidBrush circleBrush = new SolidBrush(BackColor))
            {
                e.Graphics.FillEllipse(circleBrush, cx - radius, cy - radius,
                                       radius * 2, radius * 2);
            }

            // 镜面高光（左上角小椭圆，模拟光照）
            using (SolidBrush highlight = new SolidBrush(Color.FromArgb(120, Color.White)))
            {
                e.Graphics.FillEllipse(highlight, cx - radius * 0.5f, cy - radius * 0.5f,
                                       radius * 1.2f, radius * 0.7f);
            }
        }
    }

    /// <summary>带百分比文字的进度条——自绘控件，在进度条上方叠加居中百分比文字。</summary>
    private class ProgressBarWithText : Control
    {
        private int _minimum;
        private int _maximum = 100;
        private int _value;
        private string _percentageText = "";

        public int Minimum
        {
            get => _minimum;
            set { _minimum = value; Invalidate(); }
        }

        public int Maximum
        {
            get => _maximum;
            set { _maximum = Math.Max(value, 1); Invalidate(); }
        }

        public int Value
        {
            get => _value;
            set { _value = Math.Clamp(value, _minimum, _maximum); Invalidate(); }
        }

        public string PercentageText
        {
            get => _percentageText;
            set { _percentageText = value ?? ""; Invalidate(); }
        }

        public ProgressBarWithText()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                     ControlStyles.SupportsTransparentBackColor, true);
            Height = 23;
            BackColor = Color.Transparent;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var rect = ClientRectangle;
            if (rect.Width <= 0 || rect.Height <= 0)
            {
                return;
            }

            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

            // 绘制外边框（凹陷效果）
            ControlPaint.DrawBorder3D(e.Graphics, rect, Border3DStyle.SunkenOuter);
            Rectangle innerRect = new Rectangle(rect.X + 2, rect.Y + 2,
                                          rect.Width - 4, rect.Height - 4);
            if (innerRect.Width <= 0 || innerRect.Height <= 0)
            {
                return;
            }

            // 绘制背景
            using (SolidBrush bgBrush = new SolidBrush(CloudPanColors.BackgroundWhite))
            {
                e.Graphics.FillRectangle(bgBrush, innerRect);
            }

            // 绘制进度条
            if (_maximum > _minimum && _value > _minimum)
            {
                float ratio = (float)(_value - _minimum) / (_maximum - _minimum);
                int barWidth = (int)(innerRect.Width * ratio);
                if (barWidth > 0)
                {
                    Rectangle barRect = new Rectangle(innerRect.X, innerRect.Y,
                                                barWidth, innerRect.Height);
                    using LinearGradientBrush barBrush = new LinearGradientBrush(
                        barRect, CloudPanColors.PrimaryBlue,
                        CloudPanColors.AccentBlue, LinearGradientMode.Horizontal);
                    e.Graphics.FillRectangle(barBrush, barRect);
                }
            }

            // 绘制百分比文字（白色 + 阴影轮廓）
            if (!string.IsNullOrEmpty(_percentageText))
            {
                // 阴影
                var shadowRect = rect;
                shadowRect.Offset(1, 0);
                TextRenderer.DrawText(e.Graphics, _percentageText, Font, shadowRect,
                    Color.FromArgb(80, 0, 0, 0),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.NoPadding);

                // 白色文字
                TextRenderer.DrawText(e.Graphics, _percentageText, Font, rect,
                    Color.White,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.NoPadding);
            }
        }
    }

}
