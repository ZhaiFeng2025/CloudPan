using System.Data;
using System.Drawing.Drawing2D;
using CloudPan.Server.Data;
using CloudPan.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CloudPan.Server.UI;

/// <summary>
/// 服务端管理窗口——显示运行状态、设备列表、最近日志。
/// </summary>
public class ServerWindow : Form
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
        Icon = CloudPan.Shared.UI.ServerIcons.CreateServer();
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
    }

    // ===== 具名事件处理器（CP301：避免匿名 lambda 订阅无法退订） =====

    private void EmptyStatePanel_Resize(object? sender, EventArgs e) => CenterEmptyState();

    private void ClearLogBtn_Click(object? sender, EventArgs e)
    {
        _logList.Items.Clear();
        AddLog("日志已清空");
    }

    private async void RefreshTimer_Tick(object? sender, EventArgs e) => await RefreshDataAsync();

    /// <summary>关闭按钮 → 隐藏到托盘（而非销毁窗口）。系统/任务管理器关闭时放行。</summary>
    private void Window_FormClosing(object? sender, FormClosingEventArgs e)
    {
        // Application.Exit() / 进程退出 → 允许关闭
        if (e.CloseReason == CloseReason.ApplicationExitCall
            || e.CloseReason == CloseReason.TaskManagerClosing
            || e.CloseReason == CloseReason.WindowsShutDown)
        {
            _refreshTimer.Stop();
            return;
        }
        // 用户点击 X 按钮 → 隐藏到托盘
        e.Cancel = true;
        Hide();
        AddLog("窗口已隐藏至系统托盘，左键托盘图标可重新打开");
    }

    /// <summary>最小化时 → 隐藏到托盘（服务端窗口不应占据任务栏）。</summary>
    private void Window_Resize(object? sender, EventArgs e)
    {
        if (WindowState == FormWindowState.Minimized)
        {
            Hide();
            AddLog("窗口已最小化至系统托盘");
        }
    }

    /// <summary>首次显示时：刷新数据 + 刷入缓存日志。</summary>
    private async void Window_Shown(object? sender, EventArgs e)
    {
        FlushPendingLogs();
        await RefreshDataAsync();
    }

    /// <summary>
    /// 将窗口句柄创建前缓存的消息刷入日志列表。
    /// </summary>
    private void FlushPendingLogs()
    {
        if (_pendingLogs.Count == 0) return;
        foreach (string msg in _pendingLogs)
        {
            _logList.Items.Add($"[{DateTime.Now:HH:mm:ss}] {msg}");
        }
        _pendingLogs.Clear();
    }

    /// <summary>
    /// 居中空状态面板内的图标和文字
    /// </summary>
    private void CenterEmptyState()
    {
        if (_emptyStatePanel.Width <= 0 || _emptyStatePanel.Height <= 0)
        {
            return;
        }

        int cx = _emptyStatePanel.Width / 2;
        int cy = _emptyStatePanel.Height / 2;
        _emptyIcon.Location = new Point(cx - _emptyIcon.Width / 2, cy - 70);
        _emptyTitle.Location = new Point(cx - _emptyTitle.Width / 2, cy - 10);
        _emptyHint.Location = new Point(cx - _emptyHint.Width / 2, cy + 20);
    }

    /// <summary>
    /// 为控件设置圆角区域
    /// </summary>
    private static void SetRoundedRegion(Control ctrl, int radius)
    {
        if (ctrl.Width <= 0 || ctrl.Height <= 0)
        {
            return;
        }

        using GraphicsPath path = new GraphicsPath();
        int d = radius * 2;
        path.AddArc(0, 0, d, d, 180, 90);
        path.AddArc(ctrl.Width - d - 1, 0, d, d, 270, 90);
        path.AddArc(ctrl.Width - d - 1, ctrl.Height - d - 1, d, d, 0, 90);
        path.AddArc(0, ctrl.Height - d - 1, d, d, 90, 90);
        path.CloseFigure();
        // 先释放旧 Region 再设置新 Region，防止 Resize 高频触发时 GDI 句柄泄漏
        Region? oldRegion = ctrl.Region;
        ctrl.Region = new Region(path);
        oldRegion?.Dispose();
    }

    /// <summary>
    /// 创建带圆角背景的统计卡片
    /// </summary>
    private static Label CreateStatCard(TableLayoutPanel parent, string title, string value, int col)
    {
        Panel card = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = CloudPanColors.BackgroundLight,
            Margin = new Padding(4),
            Padding = new Padding(8, 6, 8, 6)
        };
        void OnCardHandleCreated(object? s, EventArgs e)
            => SetRoundedRegion((Panel)s!, CloudPanEffects.CornerRadiusMd);
        void OnCardResize(object? s, EventArgs e)
        {
            var p = (Panel)s!;
            if (p.IsHandleCreated)
            {
                SetRoundedRegion(p, CloudPanEffects.CornerRadiusMd);
            }
        }
        card.HandleCreated += OnCardHandleCreated;
        card.Resize += OnCardResize;

        Label titleLbl = new Label
        {
            Text = title,
            Font = new Font(CloudPanFonts.FontFamily, CloudPanFonts.SizeCaption),
            ForeColor = CloudPanColors.TextMuted,
            AutoSize = true,
            BackColor = Color.Transparent
        };
        Label val = new Label
        {
            Text = value,
            Font = new Font(CloudPanFonts.FontFamily, CloudPanFonts.SizeSubtitle, FontStyle.Bold),
            AutoSize = true,
            ForeColor = CloudPanColors.SuccessGreen,
            BackColor = Color.Transparent
        };

        void CenterStatLabels()
        {
            titleLbl.Location = new Point((card.Width - titleLbl.Width) / 2, 6);
            val.Location = new Point((card.Width - val.Width) / 2, 26);
        }

        void OnCenterStatLabels(object? s, EventArgs e) => CenterStatLabels();
        card.SizeChanged += OnCenterStatLabels;
        card.HandleCreated += OnCenterStatLabels;

        card.Controls.Add(titleLbl);
        card.Controls.Add(val);
        parent.Controls.Add(card, col, 0);

        return val;
    }

    private async Task RefreshDataAsync()
    {
        try
        {
            var elapsed = DateTime.UtcNow - _startTime;
            _uptimeLabel.Text = $"{(int)elapsed.TotalHours}h {elapsed.Minutes}m";
            _uptimeLabel.ForeColor = CloudPanColors.SuccessGreen;
            _statusLabel.ForeColor = CloudPanColors.SuccessGreen;

            await using var db = await _dbFactory.CreateDbContextAsync();
            var devices = await db.Devices.OrderByDescending(d => d.LastSeen).Take(20).ToListAsync();

            _deviceList.BeginUpdate();
            _deviceList.Items.Clear();

            if (devices.Count == 0)
            {
                // 显示空状态引导
                _deviceList.Visible = false;
                _emptyStatePanel.Visible = true;
                _emptyStatePanel.BringToFront();
                CenterEmptyState();
            }
            else
            {
                // 显示设备列表
                _emptyStatePanel.Visible = false;
                _deviceList.Visible = true;
                _deviceList.BringToFront();

                foreach (var d in devices)
                {
                    bool isServer = d.Id == "server";
                    ListViewItem item = new ListViewItem(d.Name) { Tag = d };
                    item.SubItems.Add(isServer ? "服务端" : "客户端");
                    item.SubItems.Add(d.Online == 1 ? "在线" : "离线");
                    item.SubItems.Add(DateTime.TryParse(d.LastSeen, out var dt)
                        ? dt.ToLocalTime().ToString("MM-dd HH:mm") : "-");
                    item.SubItems.Add("-");  // 同步文件数（简化实现）
                    _deviceList.Items.Add(item);
                }
            }

            _deviceList.EndUpdate();

            _connLabel.Text = devices.Count(d => d.Online == 1).ToString();
        }
        catch (Exception ex)
        {
            AddLog($"刷新数据失败: {ex.Message}（5 秒后自动重试）");
            _statusLabel.ForeColor = CloudPanColors.ErrorRed;
        }
    }

    /// <summary>
    /// 追加日志（线程安全）。窗口句柄创建前调用时缓存消息，句柄创建后自动刷入。
    /// 使用 BeginInvoke 避免死锁和窗口已释放异常。
    /// </summary>
    public void AddLog(string msg)
    {
        if (IsDisposed) return;

        // 窗口句柄尚未创建 → 缓存消息
        if (!IsHandleCreated)
        {
            _pendingLogs.Add(msg);
            return;
        }

        if (InvokeRequired)
        {
            try { BeginInvoke(() => AddLog(msg)); }
            catch (ObjectDisposedException) { /* 窗口已关闭，静默放弃 */ }
            return;
        }
        _logList.Items.Add($"[{DateTime.Now:HH:mm:ss}] {msg}");
        if (_logList.Items.Count > 500)
        {
            _logList.Items.RemoveAt(0);
        }

        _logList.TopIndex = _logList.Items.Count - 1;
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
