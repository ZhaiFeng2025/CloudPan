using CloudPan.Infrastructure.Design;

namespace CloudPan.Client.UI;

/// <summary>
/// 设置窗口——同步管理、账户配置（含存储信息）、带宽限制、选择性同步。
/// </summary>
public partial class SettingsForm : Form
{
    private readonly TabControl _tabs;
    private TextBox _serverBox = null!;
    private TextBox _folderBox = null!;
    private TextBox _tokenBox = null!;
    private TextBox _uploadLimitBox = null!;
    private TextBox _downloadLimitBox = null!;
    private SelectiveSyncPanel _syncPanel = null!;
    private Button _saveBtn = null!;
    private Button _testConnBtn = null!;
    private Label _connResultIcon = null!;
    private Label _connResultText = null!;
    private Button _tokenToggleBtn = null!;
    private Label _storageSizeLabel = null!;

    // 文件夹大小缓存（5分钟有效）
    private static long CachedSize;
    private static DateTime LastSizeCheck;
    private static string CachedPath = "";

    private bool _tokenMasked = true;

    // T-074：目录树加载器（从 SyncEngine.GetDirectoryTreePathsAsync 注入），供同步页异步填充勾选树
    private readonly Func<Task<List<string>>>? _directoryTreeLoader;

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

        BuildAccountTab(serverUrl, syncRoot, token);
        BuildBandwidthTab(uploadSpeedBps, downloadSpeedBps);
        BuildSyncTab(selectedPaths);
        BuildBottomPanel();

        Controls.Add(_tabs);

        // 异步计算文件夹大小
        _ = UpdateFolderSizeAsync(syncRoot);

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
            Text = "提示：Token 修改需重启客户端后生效",
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
