using CloudPan.Infrastructure.Design;
using CloudPan.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CloudPan.Server.UI;

/// <summary>
/// 服务端管理窗口——显示运行状态、设备列表、最近日志。
/// </summary>
public partial class ServerWindow : Form
{
    private readonly Label _statusLabel;
    private readonly Label _uptimeLabel;
    private readonly Label _connLabel;
    private readonly ListView _deviceList;
    private readonly ListBox _logList;
    private readonly Button _clearLogBtn;
    private readonly Panel _emptyStatePanel;
    private readonly Label _emptyIcon;
    private readonly Label _emptyTitle;
    private readonly Label _emptyHint;
    private readonly IDbContextFactory<CloudPanDbContext> _dbFactory;
    private readonly System.Windows.Forms.Timer _refreshTimer;
    private readonly DateTime _startTime = DateTime.UtcNow;
    private readonly System.Windows.Forms.TabControl _tabs;
    private readonly SettingsPage _settingsPage;

    /// <summary>
    /// 窗口句柄创建前缓存的消息（AddLog 在窗口首次 Show 前被调用时暂存于此）。
    /// 句柄创建后自动刷入日志列表。
    /// </summary>
    private readonly List<string> _pendingLogs = new();

    public ServerWindow(IServiceProvider services, int effectivePort, string currentSyncRoot)
    {
        _dbFactory = services.GetRequiredService<IDbContextFactory<CloudPanDbContext>>();
        Text = "CloudPan 服务端 — 管理";
        Size = new Size(720, 520);
        MinimumSize = new Size(600, 400);
        StartPosition = FormStartPosition.CenterScreen;
        Icon = IconFactory.CreateServer();
        Font = new Font(CloudPanFonts.FontFamily, CloudPanFonts.SizeBody);
        BackColor = CloudPanColors.BackgroundWhite;

        // ===== 状态统计卡片行 =====
        TableLayoutPanel statPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 86,
            Padding = new Padding(12, 8, 12, 6),
            ColumnCount = 4,
            RowCount = 1
        };
        statPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25F));
        statPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25F));
        statPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25F));
        statPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25F));

        _statusLabel = CreateStatCard(statPanel, "服务状态", "运行中", 0);
        _uptimeLabel = CreateStatCard(statPanel, "运行时间", "", 1);
        _connLabel = CreateStatCard(statPanel, "在线设备", "0", 2);
        CreateStatCard(statPanel, "版本", "v1.0.0", 3);

        // ===== 设备列表 + 日志分割区域 =====
        SplitContainer split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterDistance = 240,
            Panel1MinSize = 120,
            Panel2MinSize = 80
        };

        // ---- 设备列表 ----
        _deviceList = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            BackColor = CloudPanColors.BackgroundWhite,
            BorderStyle = BorderStyle.FixedSingle,
            HeaderStyle = ColumnHeaderStyle.Clickable
        };
        _deviceList.Columns.Add("设备名称", 220);
        _deviceList.Columns.Add("类型", 80);
        _deviceList.Columns.Add("在线", 55);
        _deviceList.Columns.Add("最后在线", 150);
        _deviceList.Columns.Add("同步文件数", 100);

        // 右键菜单
        ContextMenuStrip ctxMenu = new ContextMenuStrip();
        ctxMenu.Items.Add("查看详情", null, (_, _) =>
        {
            if (_deviceList.SelectedItems.Count > 0)
            {
                MessageBox.Show("设备详情功能开发中", "CloudPan",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        });
        ctxMenu.Items.Add("强制断开", null, (_, _) =>
        {
            if (_deviceList.SelectedItems.Count > 0)
            {
                MessageBox.Show("强制断开功能开发中", "CloudPan",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        });
        _deviceList.ContextMenuStrip = ctxMenu;

        // ---- 空状态面板 ----
        _emptyStatePanel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = CloudPanColors.BackgroundWhite,
            Visible = false
        };
        _emptyIcon = new Label
        {
            Text = "☁",
            Font = new Font("Segoe UI", 52f),
            AutoSize = true,
            ForeColor = CloudPanColors.TextMuted,
            BackColor = Color.Transparent
        };
        _emptyTitle = new Label
        {
            Text = "暂无设备连接",
            Font = new Font(CloudPanFonts.FontFamily, 16f, FontStyle.Bold),
            AutoSize = true,
            ForeColor = CloudPanColors.TextSecondary,
            BackColor = Color.Transparent
        };
        _emptyHint = new Label
        {
            Text = "在客户端输入服务端地址和 Token 即可完成配对",
            Font = new Font(CloudPanFonts.FontFamily, 10f),
            AutoSize = true,
            ForeColor = CloudPanColors.TextMuted,
            BackColor = Color.Transparent
        };
        _emptyStatePanel.Controls.AddRange(new Control[] { _emptyIcon, _emptyTitle, _emptyHint });
        _emptyStatePanel.Resize += EmptyStatePanel_Resize;

        // 设备面板（切换设备列表 / 空状态）
        Panel devicePanel = new Panel { Dock = DockStyle.Fill };
        devicePanel.Controls.Add(_deviceList);
        devicePanel.Controls.Add(_emptyStatePanel);

        // ---- 日志区域 ----
        // 日志工具栏：标题左对齐 + 清空按钮右对齐
        TableLayoutPanel logToolbar = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 28,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = CloudPanColors.BackgroundGray
        };
        logToolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        logToolbar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        Label logTitle = new Label
        {
            Text = "运行日志",
            Font = new Font(CloudPanFonts.FontFamily, CloudPanFonts.SizeBodySmall, FontStyle.Bold),
            ForeColor = CloudPanColors.TextSecondary,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(8, 0, 0, 0)
        };

        _logList = new ListBox
        {
            Dock = DockStyle.Fill,
            Font = new Font(CloudPanFonts.FontFamilyMono, CloudPanFonts.SizeMono),
            BackColor = CloudPanColors.BackgroundLight,
            BorderStyle = BorderStyle.FixedSingle
        };

        _clearLogBtn = new Button
        {
            Text = "清空",
            FlatStyle = FlatStyle.Flat,
            Size = new Size(50, 22),
            Dock = DockStyle.Right,
            BackColor = CloudPanColors.BackgroundWhite,
            ForeColor = CloudPanColors.TextSecondary,
            Font = new Font(CloudPanFonts.FontFamily, CloudPanFonts.SizeCaption),
            Cursor = Cursors.Hand,
            TabStop = false,
            Margin = new Padding(0, 3, 4, 3)
        };
        _clearLogBtn.FlatAppearance.BorderColor = CloudPanColors.BorderLight;
        _clearLogBtn.Click += ClearLogBtn_Click;

        logToolbar.Controls.Add(logTitle, 0, 0);
        logToolbar.Controls.Add(_clearLogBtn, 1, 0);

        _logList.Items.Add("服务端已启动");

        Panel logPanel = new Panel { Dock = DockStyle.Fill };
        logPanel.Controls.Add(_logList);
        logPanel.Controls.Add(logToolbar);

        split.Panel1.Controls.Add(devicePanel);
        split.Panel2.Controls.Add(logPanel);

        // ===== 页签容器（概览 / 设置） =====
        _tabs = new System.Windows.Forms.TabControl { Dock = DockStyle.Fill };

        TabPage overviewTab = new TabPage("概览");
        Panel overviewHost = new Panel { Dock = DockStyle.Fill };
        // 保持与原来相同的 z-order：statPanel(Dock=Top) 在 split(Fill) 之后 Add → 占顶部
        overviewHost.Controls.Add(split);
        overviewHost.Controls.Add(statPanel);
        overviewTab.Controls.Add(overviewHost);
        _tabs.TabPages.Add(overviewTab);

        _settingsPage = new SettingsPage(services, effectivePort, currentSyncRoot, AddLog) { Dock = DockStyle.Fill };
        TabPage settingsTab = new TabPage("设置");
        settingsTab.Controls.Add(_settingsPage);
        _tabs.TabPages.Add(settingsTab);

        Controls.Add(_tabs);

        // 定时刷新
        _refreshTimer = new System.Windows.Forms.Timer { Interval = 5000 };
        _refreshTimer.Tick += RefreshTimer_Tick;
        _refreshTimer.Start();

        // ===== 窗口生命周期事件 =====

        // 关闭按钮 → 隐藏到托盘（而非销毁窗口）
        FormClosing += Window_FormClosing;

        // 最小化时 → 隐藏到托盘（用户体验：服务端窗口不应占据任务栏）
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
