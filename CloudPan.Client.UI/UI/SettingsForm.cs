using CloudPan.Infrastructure.Design;

namespace CloudPan.Client.UI;

/// <summary>
/// 设置窗口——同步管理、账户配置（含存储信息）、带宽限制、选择性同步。
/// 各 Tab 构建/事件逻辑外提为 SettingsAccountTab/SettingsBandwidthTab/SettingsSyncTab 协作类（T-109）。
/// </summary>
public partial class SettingsForm : Form
{
    internal readonly TabControl _tabs;
    internal TextBox _serverBox = null!;
    internal TextBox _folderBox = null!;
    internal TextBox _tokenBox = null!;
    internal TextBox _uploadLimitBox = null!;
    internal TextBox _downloadLimitBox = null!;
    internal SelectiveSyncPanel _syncPanel = null!;
    private Button _saveBtn = null!;
    internal Button _testConnBtn = null!;
    internal Label _connResultIcon = null!;
    internal Label _connResultText = null!;
    internal Button _tokenToggleBtn = null!;
    internal Label _storageSizeLabel = null!;

    internal bool _tokenMasked = true;

    // T-074：目录树加载器（从 SyncEngine.GetDirectoryTreePathsAsync 注入），供同步页异步填充勾选树
    internal readonly Func<Task<List<string>>>? _directoryTreeLoader;

    // T-109：各 Tab 逻辑外提协作类
    private readonly SettingsAccountTab _accountTab;
    private readonly SettingsBandwidthTab _bandwidthTab;
    private readonly SettingsSyncTab _syncTab;

    public string ServerUrl => _serverBox.Text.Trim();
    public string SyncRoot => _folderBox.Text.Trim();
    public string Token => _tokenBox.Text.Trim();
    public long UploadLimitBps => long.TryParse(_uploadLimitBox.Text.Trim(), out long v) ? v * 1024 : 0;
    public long DownloadLimitBps => long.TryParse(_downloadLimitBox.Text.Trim(), out long v) ? v * 1024 : 0;
    public List<string> SelectedPaths => _syncPanel.SelectedPaths;

    public SettingsForm(string serverUrl, string syncRoot, string token, long uploadSpeedBps, long downloadSpeedBps, List<string> selectedPaths, Func<Task<List<string>>>? directoryTreeLoader = null)
    {
        Text = "CloudPan 设置";
        Size = new Size(580, 560);
        StartPosition = FormStartPosition.CenterParent;
        Font = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;

        _directoryTreeLoader = directoryTreeLoader;
        _tabs = new TabControl { Dock = DockStyle.Fill };

        _accountTab = new SettingsAccountTab(this);
        _bandwidthTab = new SettingsBandwidthTab(this);
        _syncTab = new SettingsSyncTab(this);

        _accountTab.BuildAccountTab(serverUrl, syncRoot, token);
        _bandwidthTab.BuildBandwidthTab(uploadSpeedBps, downloadSpeedBps);
        _syncTab.BuildSyncTab(selectedPaths);
        BuildBottomPanel();

        Controls.Add(_tabs);

        // 异步计算文件夹大小
        _ = _accountTab.UpdateFolderSizeAsync(syncRoot);

        // T-032 深色模式：接入主题跟随（当前主题归一化 + 系统切换时刷新，含内部 SelectiveSyncPanel 树）
        ThemeWatcher.Watch(this);
    }

    // ──────────────────────────────────────────────
    // 底部按钮
    // ──────────────────────────────────────────────

    private void BuildBottomPanel()
    {
        TableLayoutPanel bottomPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 48,
            ColumnCount = 2,
            RowCount = 1,
        };
        bottomPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        bottomPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        Label saveHint = new Label
        {
            // T-075：统一提示——服务端地址/同步根/Token 三类修改均需重启客户端后生效
            Text = "提示：服务端地址、同步文件夹或 Token 修改后需重启客户端才生效",
            AutoSize = true,
            ForeColor = CloudPanColors.TextMuted,
            Font = new Font(SystemFonts.MessageBoxFont?.FontFamily ?? FontFamily.GenericSansSerif, 8F),
            Margin = new Padding(16, 0, 0, 0),
            TextAlign = ContentAlignment.MiddleLeft,
        };

        FlowLayoutPanel btnPanel = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            Height = 48,
            Padding = new Padding(12),
        };

        // 保存按钮（蓝色主色）
        _saveBtn = new Button
        {
            Text = "保存",
            Width = CloudPanSpacing.ButtonWidth,
            Height = CloudPanSpacing.InputHeight,
            FlatStyle = FlatStyle.Flat,
            BackColor = CloudPanColors.PrimaryBlue,
            ForeColor = CloudPanColors.TextOnPrimary,
            Font = new Font(CloudPanFonts.FontFamily, 10F, FontStyle.Bold),
            Cursor = Cursors.Hand,
            UseVisualStyleBackColor = false,
        };
        _saveBtn.FlatAppearance.BorderSize = 0;
        _saveBtn.FlatAppearance.MouseOverBackColor = CloudPanColors.PrimaryBlueHover;
        _saveBtn.FlatAppearance.MouseDownBackColor = CloudPanColors.PrimaryBluePress;
        _saveBtn.Click += SaveBtn_Click;

        // 取消按钮（与 SetupForm 样式一致）
        Button cancelBtn = new Button
        {
            Text = "取消",
            Width = CloudPanSpacing.ButtonWidth,
            Height = CloudPanSpacing.InputHeight,
            FlatStyle = FlatStyle.Flat,
            BackColor = CloudPanColors.BackgroundWhite,
            ForeColor = CloudPanColors.TextSecondary,
            Font = new Font(CloudPanFonts.FontFamily, CloudPanFonts.SizeBody),
            Cursor = Cursors.Hand,
            UseVisualStyleBackColor = false,
        };
        cancelBtn.FlatAppearance.BorderColor = CloudPanColors.BorderLight;
        cancelBtn.FlatAppearance.MouseOverBackColor = CloudPanColors.ButtonHoverBg;
        cancelBtn.FlatAppearance.MouseDownBackColor = CloudPanColors.ButtonPressBg;
        cancelBtn.Click += CancelBtn_Click;

        btnPanel.Controls.Add(_saveBtn);
        btnPanel.Controls.Add(cancelBtn);

        bottomPanel.Controls.Add(saveHint, 0, 0);
        bottomPanel.Controls.Add(btnPanel, 1, 0);

        Controls.Add(bottomPanel);
    }

    private void SaveBtn_Click(object? sender, EventArgs e)
    {
        // T-075：保存前对同步根复用 SetupForm.ValidateFolderSafety 做路径安全校验（拒存根目录/系统目录/网络盘/.cloudpan），非法路径阻止保存
        string? pathError = SetupForm.ValidateFolderSafety(SyncRoot);
        if (pathError != null)
        {
            MessageBox.Show(pathError + "\n\n同步文件夹未保存，请修改后再保存。",
                "CloudPan", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        // T-074：目录树未加载时阻止保存，避免空树全选覆盖既有排除配置
        if (!_syncPanel.IsTreeLoaded)
        {
            MessageBox.Show(_syncPanel.TreeLoadMessage + "\n\n排除设置将保持不变，请确认服务端可访问后再保存。",
                "CloudPan", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        DialogResult = DialogResult.OK;
        Close();
    }

    private void CancelBtn_Click(object? sender, EventArgs e)
    {
        DialogResult = DialogResult.Cancel;
        Close();
    }
}
