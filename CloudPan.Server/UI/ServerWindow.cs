using System.Data;
using System.Drawing.Drawing2D;
using CloudPan.Server.Data;
using CloudPan.Shared;
using Microsoft.EntityFrameworkCore;

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

    public ServerWindow(IDbContextFactory<CloudPanDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
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
        var verLabel = CreateStatCard(statPanel, "版本", "v1.0.0", 3);

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
        _emptyStatePanel.Resize += (_, _) => CenterEmptyState();

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
        _clearLogBtn.Click += (_, _) => { _logList.Items.Clear(); AddLog("日志已清空"); };

        logToolbar.Controls.Add(logTitle, 0, 0);
        logToolbar.Controls.Add(_clearLogBtn, 1, 0);

        _logList.Items.Add("服务端已启动");

        Panel logPanel = new Panel { Dock = DockStyle.Fill };
        logPanel.Controls.Add(_logList);
        logPanel.Controls.Add(logToolbar);

        split.Panel1.Controls.Add(devicePanel);
        split.Panel2.Controls.Add(logPanel);

        Controls.Add(split);
        Controls.Add(statPanel);

        // 定时刷新
        _refreshTimer = new System.Windows.Forms.Timer { Interval = 5000 };
        _refreshTimer.Tick += async (_, _) => await RefreshDataAsync();
        _refreshTimer.Start();

        FormClosing += (_, e) => { e.Cancel = true; Hide(); AddLog("窗口已隐藏至系统托盘，双击托盘图标重新打开"); };
        Load += async (_, _) => await RefreshDataAsync();
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
        card.HandleCreated += (_, _) => SetRoundedRegion(card, CloudPanEffects.CornerRadiusMd);
        card.Resize += (_, _) =>
        {
            if (card.IsHandleCreated)
            {
                SetRoundedRegion(card, CloudPanEffects.CornerRadiusMd);
            }
        };

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

        card.SizeChanged += (_, _) => CenterStatLabels();
        card.HandleCreated += (_, _) => CenterStatLabels();

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
                CenterEmptyState();
            }
            else
            {
                // 显示设备列表
                _emptyStatePanel.Visible = false;
                _deviceList.Visible = true;

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
    /// 追加日志（线程安全）。使用 BeginInvoke 避免死锁和窗口已释放异常。
    /// </summary>
    public void AddLog(string msg)
    {
        if (IsDisposed || !IsHandleCreated) return;
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
}
