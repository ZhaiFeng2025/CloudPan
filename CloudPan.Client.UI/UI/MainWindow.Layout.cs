using System.Diagnostics;
using System.Drawing.Drawing2D;
using CloudPan.Client.Core.Models;
using CloudPan.Client.Core.Services;
using CloudPan.Contract;
using CloudPan.Infrastructure.Design;

namespace CloudPan.Client.UI;

/// <summary>MainWindow 部分类：布局构建（BuildLayout）。</summary>
public partial class MainWindow
{

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

        // ── 控件入窗体（z-order：状态栏最上，分隔线次之，主区填充，撤销条最下） ──
        Controls.Add(statusTable);    // 顶部状态栏
        Controls.Add(new Panel { Dock = DockStyle.Top, Height = 1, BackColor = CloudPanColors.BorderLight }); // 状态栏与主区分隔线
        Controls.Add(_splitter);      // 主区：文件浏览 + 日志侧栏

        // T-014：撤销删除 Snackbar（底部条，默认隐藏；删除后 5 秒内显示「撤销」）
        _undoBar = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 48,
            BackColor = CloudPanColors.TextPrimary, // 深色底，对比度达标
            Visible = false,
        };
        _undoLabel = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = CloudPanColors.TextOnPrimary,
            Font = new Font(baseFont.FontFamily, CloudPanFonts.SizeBody),
            Padding = new Padding(16, 0, 0, 0),
        };
        _undoButton = new Button
        {
            Text = "撤销",
            Dock = DockStyle.Right,
            Width = 88,
            Height = CloudPanSpacing.MinTouchSize,
            FlatStyle = FlatStyle.Flat,
            Margin = new Padding(0, 0, 12, 0),
            UseVisualStyleBackColor = false,
            BackColor = CloudPanColors.SuccessGreen,
            ForeColor = CloudPanColors.TextOnPrimary,
        };
        _undoButton.FlatAppearance.BorderColor = CloudPanColors.SuccessGreen;
        _undoButton.Click += UndoButton_Click;
        _undoBar.Controls.Add(_undoLabel);
        _undoBar.Controls.Add(_undoButton);
        Controls.Add(_undoBar); // Dock.Bottom：Visible=false 不占空间，显示时占底部 48px
    }
}
