using CloudPan.Contract;
using CloudPan.Infrastructure.Design;

namespace CloudPan.Server.UI;

/// <summary>设置页表单渲染协作类（T-110）：契约驱动分组渲染、Startup 字段/Secret 字段构建与布局辅助。逻辑从 SettingsPage 外提。</summary>
internal sealed class SettingsFormBuilder
{
    private readonly SettingsPage _form;

    public SettingsFormBuilder(SettingsPage form)
    {
        _form = form;
    }

    /// <summary>分区显示名（shared-spec.json settings.groups.label 未进生成产物，UI 本地映射）。</summary>
    private static readonly IReadOnlyDictionary<SettingGroup, string> GroupTitles =
        new Dictionary<SettingGroup, string>
        {
            [SettingGroup.Network] = "网络",
            [SettingGroup.Storage] = "存储",
            [SettingGroup.Security] = "安全",
        };

    /// <summary>按分组遍历 SpecSettings.All 渲染设置区（Secret 设置 → 只读行 + 动作行；Startup 设置 → 编辑框）。</summary>
    internal void BuildSections(TableLayoutPanel root, ref int row)
    {
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
                        root.Controls.Add(_form._rotateBtn, 0, row);
                        root.Controls.Add(_form._disconnectCheck, 1, row);
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
    }

    /// <summary>Startup 持久化设置输入控件：Int→数字框（Min/Max 校验）、String→文本框（IsPath 带浏览按钮）。</summary>
    private Control CreateStartupField(ServerSettingDef def)
    {
        switch (def.Type)
        {
            case SettingType.Int:
                // 局部捕获 MBRO 类的值类型字段再调用成员，规避 CS1690
                int effectivePort = _form._effectivePort;
                TextBox intBox = new TextBox
                {
                    Text = def.Key == SpecSettings.Keys.Port ? effectivePort.ToString() : def.Default,
                    Width = 120,
                    Height = CloudPanSpacing.InputHeight,
                    Font = new Font(CloudPanFonts.FontFamily, CloudPanFonts.SizeBody),
                    BackColor = CloudPanColors.BackgroundWhite
                };
                intBox.KeyPress += SettingsPage.NumericOnly_KeyPress;
                _form._startupBoxes[def.Key] = intBox;
                return intBox;
            case SettingType.String:
                TextBox box = new TextBox
                {
                    Text = def.Key == SpecSettings.Keys.SyncRoot ? _form._currentSyncRoot : def.Default,
                    Dock = DockStyle.Fill,
                    Font = new Font(CloudPanFonts.FontFamily, CloudPanFonts.SizeBody),
                    BackColor = CloudPanColors.BackgroundWhite
                };
                _form._startupBoxes[def.Key] = box;
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
                browseBtn.Click += _form.BrowseBtn_Click;
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
        _form._tokenBox = new TextBox
        {
            ReadOnly = true,
            UseSystemPasswordChar = true,
            Width = 320,
            Height = CloudPanSpacing.InputHeight,
            Font = new Font(CloudPanFonts.FontFamilyMono, CloudPanFonts.SizeMono),
            BackColor = CloudPanColors.BackgroundGray
        };
        _form._toggleTokenBtn = new Button
        {
            Text = "显示",
            FlatStyle = FlatStyle.Flat,
            Width = 60,
            Height = CloudPanSpacing.InputHeight,
            Cursor = Cursors.Hand,
            Margin = new Padding(6, 0, 0, 0)
        };
        _form._toggleTokenBtn.Click += _form.ToggleTokenMask_Click;
        Button copyTokenBtn = new Button
        {
            Text = "复制",
            FlatStyle = FlatStyle.Flat,
            Width = 60,
            Height = CloudPanSpacing.InputHeight,
            Cursor = Cursors.Hand,
            Margin = new Padding(6, 0, 0, 0)
        };
        copyTokenBtn.Click += _form.CopyToken_Click;
        Panel tokenRow = new Panel { Dock = DockStyle.Fill, Height = CloudPanSpacing.InputHeight };
        tokenRow.Controls.Add(copyTokenBtn);
        tokenRow.Controls.Add(_form._toggleTokenBtn);
        tokenRow.Controls.Add(_form._tokenBox);
        copyTokenBtn.Dock = DockStyle.Right;
        _form._toggleTokenBtn.Dock = DockStyle.Right;
        _form._tokenBox.Dock = DockStyle.Fill;
        if (def.Action == "rotate")
        {
            _form._rotateBtn = new Button
            {
                Text = "轮换 Token",
                FlatStyle = FlatStyle.Flat,
                BackColor = CloudPanColors.ErrorBgLight,
                ForeColor = CloudPanColors.ErrorRed,
                Width = 110,
                Height = 32,
                Cursor = Cursors.Hand
            };
            _form._rotateBtn.Click += _form.RotateBtn_Click;
            _form._disconnectCheck = new CheckBox
            {
                Text = "同时断开所有已连接设备",
                AutoSize = true,
                ForeColor = CloudPanColors.TextSecondary,
                Font = new Font(CloudPanFonts.FontFamily, CloudPanFonts.SizeBodySmall)
            };
        }
        return tokenRow;
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
}
