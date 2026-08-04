using CloudPan.Infrastructure.Design;
using CloudPan.Server.Core;

namespace CloudPan.Server.UI;

/// <summary>管理窗口布局协作类（T-110）：状态卡/设备列表与右键菜单/空状态/日志区/页签与设置页构建。逻辑从 ServerWindow 外提。</summary>
internal sealed class ServerWindowLayout
{
    private readonly ServerWindow _form;

    public ServerWindowLayout(ServerWindow form)
    {
        _form = form;
    }

    /// <summary>构建管理窗口完整控件树（状态卡、设备列表+右键菜单、日志区、页签、设置页、定时器）。</summary>
    internal void Build(ITokenService tokenService, IServerStatusService statusService, int effectivePort, string currentSyncRoot)
    {
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

        _form._statusLabel = ServerStatCards.CreateStatCard(statPanel, "服务状态", "运行中", 0);
        _form._uptimeLabel = ServerStatCards.CreateStatCard(statPanel, "运行时间", "", 1);
        _form._connLabel = ServerStatCards.CreateStatCard(statPanel, "在线设备", "0", 2);
        ServerStatCards.CreateStatCard(statPanel, "版本", "v1.0.0", 3);

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
        _form._deviceList = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            BackColor = CloudPanColors.BackgroundWhite,
            BorderStyle = BorderStyle.FixedSingle,
            HeaderStyle = ColumnHeaderStyle.Clickable
        };
        _form._deviceList.Columns.Add("设备名称", 220);
        _form._deviceList.Columns.Add("类型", 80);
        _form._deviceList.Columns.Add("在线", 55);
        _form._deviceList.Columns.Add("最后在线", 150);
        _form._deviceList.Columns.Add("同步文件数", 100);

        // 右键菜单（功能开发中占位，逻辑外提至布局协作类）
        ContextMenuStrip ctxMenu = new ContextMenuStrip();
        ctxMenu.Items.Add("查看详情", null, (_, _) =>
        {
            if (_form._deviceList.SelectedItems.Count > 0)
            {
                MessageBox.Show("设备详情功能开发中", "CloudPan",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        });
        ctxMenu.Items.Add("强制断开", null, (_, _) =>
        {
            if (_form._deviceList.SelectedItems.Count > 0)
            {
                MessageBox.Show("强制断开功能开发中", "CloudPan",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        });
        _form._deviceList.ContextMenuStrip = ctxMenu;

        // ---- 空状态面板 ----
        _form._emptyStatePanel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = CloudPanColors.BackgroundWhite,
            Visible = false
        };
        _form._emptyIcon = new Label
        {
            Text = "☁",
            Font = new Font("Segoe UI", 52f),
            AutoSize = true,
            ForeColor = CloudPanColors.TextMuted,
            BackColor = Color.Transparent
        };
        _form._emptyTitle = new Label
        {
            Text = "暂无设备连接",
            Font = new Font(CloudPanFonts.FontFamily, 16f, FontStyle.Bold),
            AutoSize = true,
            ForeColor = CloudPanColors.TextSecondary,
            BackColor = Color.Transparent
        };
        _form._emptyHint = new Label
        {
            Text = "在客户端输入服务端地址和 Token 即可完成配对",
            Font = new Font(CloudPanFonts.FontFamily, 10f),
            AutoSize = true,
            ForeColor = CloudPanColors.TextMuted,
            BackColor = Color.Transparent
        };
        _form._emptyStatePanel.Controls.AddRange(new Control[] { _form._emptyIcon, _form._emptyTitle, _form._emptyHint });

        // 设备面板（切换设备列表 / 空状态）
        Panel devicePanel = new Panel { Dock = DockStyle.Fill };
        devicePanel.Controls.Add(_form._deviceList);
        devicePanel.Controls.Add(_form._emptyStatePanel);

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

        _form._logList = new ListBox
        {
            Dock = DockStyle.Fill,
            Font = new Font(CloudPanFonts.FontFamilyMono, CloudPanFonts.SizeMono),
            BackColor = CloudPanColors.BackgroundLight,
            BorderStyle = BorderStyle.FixedSingle
        };

        _form._clearLogBtn = new Button
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
        _form._clearLogBtn.FlatAppearance.BorderColor = CloudPanColors.BorderLight;

        logToolbar.Controls.Add(logTitle, 0, 0);
        logToolbar.Controls.Add(_form._clearLogBtn, 1, 0);

        _form._logList.Items.Add("服务端已启动");

        Panel logPanel = new Panel { Dock = DockStyle.Fill };
        logPanel.Controls.Add(_form._logList);
        logPanel.Controls.Add(logToolbar);

        split.Panel1.Controls.Add(devicePanel);
        split.Panel2.Controls.Add(logPanel);

        // ===== 页签容器（概览 / 设置） =====
        _form._tabs = new System.Windows.Forms.TabControl { Dock = DockStyle.Fill };

        TabPage overviewTab = new TabPage("概览");
        Panel overviewHost = new Panel { Dock = DockStyle.Fill };
        // 保持与原来相同的 z-order：statPanel(Dock=Top) 在 split(Fill) 之后 Add → 占顶部
        overviewHost.Controls.Add(split);
        overviewHost.Controls.Add(statPanel);
        overviewTab.Controls.Add(overviewHost);
        _form._tabs.TabPages.Add(overviewTab);

        _form._settingsPage = new SettingsPage(tokenService, statusService, effectivePort, currentSyncRoot, _form.AddLog) { Dock = DockStyle.Fill };
        TabPage settingsTab = new TabPage("设置");
        settingsTab.Controls.Add(_form._settingsPage);
        _form._tabs.TabPages.Add(settingsTab);

        _form.Controls.Add(_form._tabs);

        // 定时刷新（Tick 订阅保留在 ServerWindow 声明类）
        _form._refreshTimer = new System.Windows.Forms.Timer { Interval = 5000 };
    }
}
