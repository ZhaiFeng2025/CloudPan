using System.Net;
using System.Net.Sockets;
using CloudPan.Contract;
using CloudPan.Infrastructure.Configuration;
using CloudPan.Infrastructure.Design;
using CloudPan.Server.Core;
using CloudPan.Server.Host.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace CloudPan.Server.UI;

/// <summary>
/// 服务端设置页。表单由 shared-spec.json → SpecSettings.All 契约驱动生成（键名/标签/校验/持久化
/// 通道均来自设置定义），禁止手写设置键字符串（规则 0）；Startup 设置经 ServerSettingsFile 重启生效，
/// AppConfig 运行时设置经 ISettingsService（token_hash 轮换由 ITokenService 写入）。
/// </summary>
public class SettingsPage : UserControl
{
    private readonly ITokenService _tokenService;
    private readonly Action<string> _log;
    private readonly int _effectivePort;
    private readonly string _currentSyncRoot;

    /// <summary>Startup 持久化设置编辑框（按 SpecSettings.Keys）。保存经 ServerSettingsFile，重启生效。</summary>
    private readonly Dictionary<string, TextBox> _startupBoxes = new(StringComparer.Ordinal);

    // token_hash（Secret/AppConfig，action=rotate）专用控件
    private TextBox _tokenBox = null!;
    private CheckBox _disconnectCheck = null!;
    private Button _toggleTokenBtn = null!;
    private Button _rotateBtn = null!;
    private readonly Label _statusLabel;

    /// <summary>分区显示名（shared-spec.json settings.groups.label 未进生成产物，UI 本地映射）。</summary>
    private static readonly IReadOnlyDictionary<SettingGroup, string> GroupTitles =
        new Dictionary<SettingGroup, string>
        {
            [SettingGroup.Network] = "网络",
            [SettingGroup.Storage] = "存储",
            [SettingGroup.Security] = "安全",
        };

    public SettingsPage(IServiceProvider services, int effectivePort, string currentSyncRoot, Action<string> log)
    {
        _tokenService = services.GetRequiredService<ITokenService>();
        _effectivePort = effectivePort;
        _currentSyncRoot = currentSyncRoot;
        _log = log;

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

        // ===== 契约驱动表单：遍历 SpecSettings.All 按分组渲染 =====
        foreach (SettingGroup group in Enum.GetValues<SettingGroup>())
        {
            AddSectionTitle(root, ref row, GroupTitles[group]);
            foreach (ServerSettingDef def in SpecSettings.All.Where(d => d.Group == group))
            {
                if (def.Type == SettingType.Secret)
                {
                    // Secret 设置（token_hash）：只读展示行 + Action 动作行（rotate → 轮换按钮 + 断开选项）
                    Control field = CreateSecretField(def);
                    AddFieldRow(root, ref row, def.Label, field);
                    if (def.Action == "rotate")
                    {
                        root.Controls.Add(_rotateBtn, 0, row);
                        root.Controls.Add(_disconnectCheck, 1, row);
                        row++;
                    }
                }
                else
                {
                    // Startup 持久化设置（端口/同步根目录）
                    AddFieldRow(root, ref row, def.Label, CreateStartupField(def));
                }
                AddHint(root, ref row, def.Description, CloudPanColors.TextMuted);
            }
        }

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

    /// <summary>Startup 持久化设置输入控件：Int→数字框（Min/Max 校验）、String→文本框（IsPath 带浏览按钮）。</summary>
    private Control CreateStartupField(ServerSettingDef def)
    {
        switch (def.Type)
        {
            case SettingType.Int:
                TextBox intBox = new TextBox
                {
                    Text = def.Key == SpecSettings.Keys.Port ? _effectivePort.ToString() : def.Default,
                    Width = 120,
                    Height = CloudPanSpacing.InputHeight,
                    Font = new Font(CloudPanFonts.FontFamily, CloudPanFonts.SizeBody),
                    BackColor = CloudPanColors.BackgroundWhite
                };
                intBox.KeyPress += NumericOnly_KeyPress;
                _startupBoxes[def.Key] = intBox;
                return intBox;
            case SettingType.String:
                TextBox box = new TextBox
                {
                    Text = def.Key == SpecSettings.Keys.SyncRoot ? _currentSyncRoot : def.Default,
                    Dock = DockStyle.Fill,
                    Font = new Font(CloudPanFonts.FontFamily, CloudPanFonts.SizeBody),
                    BackColor = CloudPanColors.BackgroundWhite
                };
                _startupBoxes[def.Key] = box;
                if (!def.IsPath)
                    return box;
                Button browseBtn = new Button
                {
                    Text = "浏览...",
                    FlatStyle = FlatStyle.Flat,
                    Width = 80,
                    Height = CloudPanSpacing.InputHeight,
                    Dock = DockStyle.Right,
                    Cursor = Cursors.Hand,
                    Tag = box
                };
                browseBtn.Click += BrowseBtn_Click;
                Panel row = new Panel { Dock = DockStyle.Fill, Height = CloudPanSpacing.InputHeight };
                row.Controls.Add(box);
                row.Controls.Add(browseBtn);
                return row;
            default:
                throw new InvalidOperationException($"Startup 持久化设置不支持的类型: {def.Type}");
        }
    }

    /// <summary>Secret 设置（token_hash）输入区：只读密码框 + 显示/复制按钮；action=rotate 时附轮换按钮与断开选项。</summary>
    private Control CreateSecretField(ServerSettingDef def)
    {
        _tokenBox = new TextBox
        {
            ReadOnly = true,
            UseSystemPasswordChar = true,
            Width = 320,
            Height = CloudPanSpacing.InputHeight,
            Font = new Font(CloudPanFonts.FontFamilyMono, CloudPanFonts.SizeMono),
            BackColor = CloudPanColors.BackgroundGray
        };
        _toggleTokenBtn = new Button
        {
            Text = "显示",
            FlatStyle = FlatStyle.Flat,
            Width = 60,
            Height = CloudPanSpacing.InputHeight,
            Cursor = Cursors.Hand,
            Margin = new Padding(6, 0, 0, 0)
        };
        _toggleTokenBtn.Click += ToggleTokenMask_Click;
        Button copyTokenBtn = new Button
        {
            Text = "复制",
            FlatStyle = FlatStyle.Flat,
            Width = 60,
            Height = CloudPanSpacing.InputHeight,
            Cursor = Cursors.Hand,
            Margin = new Padding(6, 0, 0, 0)
        };
        copyTokenBtn.Click += CopyToken_Click;
        Panel tokenRow = new Panel { Dock = DockStyle.Fill, Height = CloudPanSpacing.InputHeight };
        tokenRow.Controls.Add(copyTokenBtn);
        tokenRow.Controls.Add(_toggleTokenBtn);
        tokenRow.Controls.Add(_tokenBox);
        copyTokenBtn.Dock = DockStyle.Right;
        _toggleTokenBtn.Dock = DockStyle.Right;
        _tokenBox.Dock = DockStyle.Fill;
        if (def.Action == "rotate")
        {
            _rotateBtn = new Button
            {
                Text = "轮换 Token",
                FlatStyle = FlatStyle.Flat,
                BackColor = CloudPanColors.ErrorBgLight,
                ForeColor = CloudPanColors.ErrorRed,
                Width = 110,
                Height = 32,
                Cursor = Cursors.Hand
            };
            _rotateBtn.Click += RotateBtn_Click;
            _disconnectCheck = new CheckBox
            {
                Text = "同时断开所有已连接设备",
                AutoSize = true,
                ForeColor = CloudPanColors.TextSecondary,
                Font = new Font(CloudPanFonts.FontFamily, CloudPanFonts.SizeBodySmall)
            };
        }
        return tokenRow;
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

    // ===== 布局辅助 =====
    private static void AddSectionTitle(TableLayoutPanel root, ref int row, string title)
    {
        Label lbl = new Label
        {
            Text = title,
            Font = new Font(CloudPanFonts.FontFamily, CloudPanFonts.SizeSubtitle, FontStyle.Bold),
            ForeColor = CloudPanColors.TextPrimary,
            AutoSize = true,
            Margin = new Padding(0, CloudPanSpacing.GroupSpacing, 0, CloudPanSpacing.ElementSpacing)
        };
        root.Controls.Add(lbl, 0, row);
        root.SetColumnSpan(lbl, 2);
        row++;
    }

    private static void AddFieldRow(TableLayoutPanel root, ref int row, string label, Control field)
    {
        Label lbl = new Label
        {
            Text = label,
            AutoSize = true,
            Font = new Font(CloudPanFonts.FontFamily, CloudPanFonts.SizeBody),
            ForeColor = CloudPanColors.TextSecondary,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 8, 12, 0)
        };
        root.Controls.Add(lbl, 0, row);
        root.Controls.Add(field, 1, row);
        row++;
    }

    private static void AddHint(TableLayoutPanel root, ref int row, string text, Color color)
    {
        Label lbl = new Label
        {
            Text = text,
            AutoSize = true,
            Font = new Font(CloudPanFonts.FontFamily, CloudPanFonts.SizeBodySmall),
            ForeColor = color,
            Margin = new Padding(0, 2, 0, 0)
        };
        root.Controls.Add(lbl, 1, row);
        row++;
    }

    private void SetStatus(string text, Color color)
    {
        _statusLabel.Text = text;
        _statusLabel.ForeColor = color;
    }

    // ===== 端口校验 =====
    private static void NumericOnly_KeyPress(object? sender, KeyPressEventArgs e)
    {
        if (!char.IsDigit(e.KeyChar) && !char.IsControl(e.KeyChar)) e.Handled = true;
    }

    /// <summary>探测端口是否被占用（不含当前生效端口——本进程正在监听）。</summary>
    private static bool IsPortInUse(int port, int currentEffectivePort)
    {
        if (port == currentEffectivePort) return false;
        try
        {
            var listener = new TcpListener(IPAddress.Any, port);
            listener.Start();
            listener.Stop();
            return false;
        }
        catch (SocketException)
        {
            return true;
        }
    }

    // ===== 事件处理 =====
    private void BrowseBtn_Click(object? sender, EventArgs e)
    {
        if (sender is not Button { Tag: TextBox box })
        {
            return;
        }
        using FolderBrowserDialog dialog = new FolderBrowserDialog
        {
            Description = "选择同步根目录",
            SelectedPath = Directory.Exists(box.Text) ? box.Text : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            UseDescriptionForTitle = true
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            box.Text = dialog.SelectedPath;
        }
    }

    private void ToggleTokenMask_Click(object? sender, EventArgs e)
    {
        _tokenBox.UseSystemPasswordChar = !_tokenBox.UseSystemPasswordChar;
        _toggleTokenBtn.Text = _tokenBox.UseSystemPasswordChar ? "显示" : "隐藏";
    }

    private void CopyToken_Click(object? sender, EventArgs e)
    {
        if (string.IsNullOrEmpty(_tokenBox.Text))
        {
            SetStatus("Token 尚未生成", CloudPanColors.WarningOrange);
            return;
        }
        try { Clipboard.SetText(_tokenBox.Text); SetStatus("Token 已复制到剪贴板", CloudPanColors.SuccessGreen); }
        catch (Exception ex) { SetStatus($"复制失败: {ex.Message}", CloudPanColors.ErrorRed); }
    }

    private async void RotateBtn_Click(object? sender, EventArgs e)
    {
        var result = MessageBox.Show(
            "将重新生成家庭共享 Token，所有客户端需使用新 Token 重新配置。\n\n确定继续吗？",
            "轮换 Token", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
        if (result != DialogResult.OK)
        {
            return;
        }
        _rotateBtn.Enabled = false;
        try
        {
            string newToken = await _tokenService.RotateAsync(_disconnectCheck.Checked);
            _tokenBox.Text = newToken;
            ServerTrayApp.Token = newToken;
            _log("Token 已轮换（旧 Token 已失效）");
            SetStatus("轮换成功，旧 Token 已失效", CloudPanColors.SuccessGreen);
        }
        catch (Exception ex)
        {
            _log($"Token 轮换失败: {ex.Message}");
            SetStatus($"轮换失败: {ex.Message}", CloudPanColors.ErrorRed);
        }
        finally
        {
            _rotateBtn.Enabled = true;
        }
    }

    private void SaveBtn_Click(object? sender, EventArgs e)
    {
        // 收集并校验 Startup 持久化设置（端口/同步根目录），经 ServerSettingsFile 保存、重启生效；AppConfig 运行时设置经轮换动作即时写入
        BootstrapSettings settings = new(null, null);
        foreach (ServerSettingDef def in SpecSettings.All)
        {
            if (def.Persistence != SettingPersistence.Startup)
                continue;
            string raw = _startupBoxes[def.Key].Text.Trim();
            switch (def.Type)
            {
                case SettingType.Int:
                    if (!int.TryParse(raw, out int newPort)
                        || (def.Min.HasValue && newPort < def.Min.Value)
                        || (def.Max.HasValue && newPort > def.Max.Value))
                    {
                        SetStatus($"{def.Label} 必须是 {def.Min}-{def.Max} 的整数", CloudPanColors.ErrorRed);
                        return;
                    }
                    if (def.Key == SpecSettings.Keys.Port && IsPortInUse(newPort, _effectivePort))
                    {
                        SetStatus($"端口 {newPort} 已被占用，请更换端口", CloudPanColors.ErrorRed);
                        return;
                    }
                    settings = settings with { Port = newPort };
                    break;
                case SettingType.String:
                    string fullNewValue;
                    try
                    {
                        if (string.IsNullOrWhiteSpace(raw)) throw new ArgumentException("不能为空");
                        fullNewValue = def.IsPath ? Path.GetFullPath(raw) : raw;
                    }
                    catch (Exception ex)
                    {
                        SetStatus($"{def.Label} 无效: {ex.Message}", CloudPanColors.ErrorRed);
                        return;
                    }
                    settings = settings with { SyncRoot = fullNewValue };
                    break;
                case SettingType.Secret:
                    // Secret 运行时设置由对应 Action 处理（token 轮换），不在保存按钮写入
                    break;
            }
        }

        // 同步根目录变更警告（旧目录 .cloudpan 数据不会迁移）+ 旧服务启动参数提示
        if (settings.SyncRoot != null)
        {
            bool syncRootChanged = !string.Equals(
                Path.GetFullPath(_currentSyncRoot), settings.SyncRoot, StringComparison.OrdinalIgnoreCase);
            if (syncRootChanged)
            {
                var warning = MessageBox.Show(
                    "更改同步根目录后，新目录将从空开始重新同步（新数据库、新 Token）。\n\n" +
                    "旧目录中的 .cloudpan（数据库/版本历史/Token）不会被迁移或删除。\n\n" +
                    "确定继续吗？",
                    "更改同步根目录", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
                if (warning != DialogResult.OK)
                {
                    return;
                }
                // 旧安装的 binPath 带 --SyncRoot 会覆盖设置文件——提示重装迁移
                if (TrayAppRunner.IsServiceInstalled("CloudPanServer")
                    && ServiceRestartHelper.ServiceHasLegacyBinPathParam("--SyncRoot"))
                {
                    MessageBox.Show(
                        "检测到服务启动参数含旧的 --SyncRoot，会覆盖此处设置的同步根目录。\n\n" +
                        "请重新运行安装向导（或删除后重装 CloudPanServer 服务），以让新目录生效。",
                        "服务配置提示", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
                }
            }
        }

        // 保存（Startup 设置经 ServerSettingsFile）
        try
        {
            ServerSettingsFile.Save(settings);
            SetStatus("已保存，重启服务后生效", CloudPanColors.SuccessGreen);
            _log($"设置已保存：端口 {settings.Port}，同步根目录 {settings.SyncRoot}");
            ServiceRestartHelper.PromptRestart();
        }
        catch (Exception ex)
        {
            SetStatus($"保存失败: {ex.Message}", CloudPanColors.ErrorRed);
        }
    }

}
