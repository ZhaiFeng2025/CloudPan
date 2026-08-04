using CloudPan.Contract;
using CloudPan.Infrastructure.Design;
using CloudPan.Server.Core;

namespace CloudPan.Server.UI;

/// <summary>
/// 服务端设置页。表单由 shared-spec.json → SpecSettings.All 契约驱动生成（键名/标签/校验/持久化
/// 通道均来自设置定义），禁止手写设置键字符串（规则 0）；Startup 设置经 ServerSettingsFile 重启生效，
/// AppConfig 运行时设置经 ISettingsService（token_hash 轮换由 ITokenService 写入）。
/// 表单渲染/保存逻辑/影响面引导外提为 SettingsFormBuilder/SettingsSaveLogic/SettingsPageGuides 协作类（T-110）。
/// </summary>
public partial class SettingsPage : UserControl
{
    internal readonly ITokenService _tokenService;
    internal readonly IServerStatusService _statusService;
    internal readonly Action<string> _log;
    internal readonly int _effectivePort;
    internal readonly string _currentSyncRoot;

    /// <summary>Startup 持久化设置编辑框（按 SpecSettings.Keys）。保存经 ServerSettingsFile，重启生效。</summary>
    internal readonly Dictionary<string, TextBox> _startupBoxes = new(StringComparer.Ordinal);

    // token_hash（Secret/AppConfig，action=rotate）专用控件
    internal TextBox _tokenBox = null!;
    internal CheckBox _disconnectCheck = null!;
    internal Button _toggleTokenBtn = null!;
    internal Button _rotateBtn = null!;
    internal readonly Label _statusLabel = null!;

    // T-110：渲染/保存/影响面引导外提协作类（只存引用，惰性访问控件）
    private readonly SettingsFormBuilder _builder;
    private readonly SettingsSaveLogic _save;
    private readonly SettingsPageGuides _guide;

    public SettingsPage(ITokenService tokenService, IServerStatusService statusService, int effectivePort, string currentSyncRoot, Action<string> log)
    {
        _tokenService = tokenService;
        _statusService = statusService;
        _effectivePort = effectivePort;
        _currentSyncRoot = currentSyncRoot;
        _log = log;

        // 职责外提协作类（T-110）：只存引用，控件在下方由 builder 惰性构建
        _guide = new SettingsPageGuides(this);
        _builder = new SettingsFormBuilder(this);
        _save = new SettingsSaveLogic(this, _guide);

        BackColor = CloudPanColors.BackgroundWhite;
        AutoScroll = true;
        Padding = new Padding(CloudPanSpacing.MarginStandard);

        TableLayoutPanel root = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));

        int row = 0;

        // ===== 契约驱动表单：遍历 SpecSettings.All 按分组渲染（T-110 外提至 SettingsFormBuilder） =====
        _builder.BuildSections(root, ref row);

        // ===== 底部操作区 =====
        Button saveBtn = new Button
        {
            Text = "保存设置",
            FlatStyle = FlatStyle.Flat,
            BackColor = CloudPanColors.PrimaryBlue,
            ForeColor = CloudPanColors.TextOnPrimary,
            Width = CloudPanSpacing.ButtonWidth,
            Height = 34,
            Cursor = Cursors.Hand,
            Margin = new Padding(0, CloudPanSpacing.ElementSpacing, 0, 0)
        };
        saveBtn.Click += SaveBtn_Click;
        root.Controls.Add(saveBtn, 0, row);

        _statusLabel = new Label
        {
            AutoSize = true,
            Font = new Font(CloudPanFonts.FontFamily, CloudPanFonts.SizeBodySmall),
            ForeColor = CloudPanColors.TextSecondary,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(12, 0, 0, 0)
        };
        root.Controls.Add(_statusLabel, 1, row);
        row++;

        Controls.Add(root);
        Load += SettingsPage_Load;
    }

    // ===== 数据加载 =====
    private async void SettingsPage_Load(object? sender, EventArgs e)
    {
        try
        {
            string? token = await _tokenService.GetCurrentTokenAsync();
            if (!string.IsNullOrEmpty(token))
                _tokenBox.Text = token;
        }
        catch (Exception ex)
        {
            SetStatus($"读取 Token 失败: {ex.Message}", CloudPanColors.ErrorRed);
        }
    }

    internal void SetStatus(string text, Color color)
    {
        _statusLabel.Text = text;
        _statusLabel.ForeColor = color;
    }
}
