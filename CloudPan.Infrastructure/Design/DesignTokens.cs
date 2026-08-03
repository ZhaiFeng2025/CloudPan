using System.Drawing;

namespace CloudPan.Infrastructure.Design;

/// <summary>
/// 应用调色板设计令牌。
/// 手工维护（shared-spec.json 不含 designTokens 节，此前“从契约生成”注释为伪称），
/// 为两端 UI（CloudPan.Client.UI / CloudPan.Server.UI）共享的唯一来源：改此处两端同时生效。
/// </summary>
public static class CloudPanColors
{
    // -- 主题选择 --
    // 令牌层整体切换：所有语义色按当前系统主题在浅/深两组间解析（visual-design-kb §6），
    // 消费处无需硬编码反色。切换由 ThemeManager 监听 SystemEvents.UserPreferenceChanged 驱动。

    /// <summary>当前是否为深色主题（随系统，见 <see cref="ThemeManager"/>）。</summary>
    public static bool IsDark => ThemeManager.IsDark;

    /// <summary>按当前主题从浅/深两组中取色。</summary>
    private static Color Pick(Color light, Color dark) => ThemeManager.IsDark ? dark : light;

    // -- 主色调 --
    public static Color PrimaryBlue => Pick(Light.PrimaryBlue, Dark.PrimaryBlue);
    public static Color PrimaryBlueHover => Pick(Light.PrimaryBlueHover, Dark.PrimaryBlueHover);
    public static Color PrimaryBluePress => Pick(Light.PrimaryBluePress, Dark.PrimaryBluePress);

    // -- 状态色 --
    public static Color SuccessGreen => Pick(Light.SuccessGreen, Dark.SuccessGreen);
    public static Color WarningOrange => Pick(Light.WarningOrange, Dark.WarningOrange);
    public static Color ErrorRed => Pick(Light.ErrorRed, Dark.ErrorRed);
    public static Color InfoBlue => Pick(Light.InfoBlue, Dark.InfoBlue);

    // -- 状态底色（浅色组为浅底，深色组为深底，语义均为“状态信息底色”） --
    public static Color ErrorBgLight => Pick(Light.ErrorBgLight, Dark.ErrorBgLight);
    public static Color WarningBgLight => Pick(Light.WarningBgLight, Dark.WarningBgLight);
    public static Color SuccessBgLight => Pick(Light.SuccessBgLight, Dark.SuccessBgLight);
    public static Color InfoBgLight => Pick(Light.InfoBgLight, Dark.InfoBgLight);

    // -- 辅助色 --
    public static Color AccentBlue => Pick(Light.AccentBlue, Dark.AccentBlue);

    // -- 文字色 --
    public static Color TextPrimary => Pick(Light.TextPrimary, Dark.TextPrimary);
    public static Color TextSecondary => Pick(Light.TextSecondary, Dark.TextSecondary);
    public static Color TextMuted => Pick(Light.TextMuted, Dark.TextMuted);
    public static Color TextError => Pick(Light.TextError, Dark.TextError);
    public static Color TextDarkGray => Pick(Light.TextDarkGray, Dark.TextDarkGray);
    public static Color TextOnPrimary => Pick(Light.TextOnPrimary, Dark.TextOnPrimary);
    public static Color TextLink => Pick(Light.TextLink, Dark.TextLink);

    // -- 边框与背景 --
    public static Color BorderLight => Pick(Light.BorderLight, Dark.BorderLight);
    public static Color BorderMid => Pick(Light.BorderMid, Dark.BorderMid);
    public static Color BorderFocus => Pick(Light.BorderFocus, Dark.BorderFocus);
    public static Color BackgroundLight => Pick(Light.BackgroundLight, Dark.BackgroundLight);
    public static Color BackgroundWhite => Pick(Light.BackgroundWhite, Dark.BackgroundWhite);
    public static Color BackgroundGray => Pick(Light.BackgroundGray, Dark.BackgroundGray);
    public static Color BackgroundHover => Pick(Light.BackgroundHover, Dark.BackgroundHover);
    public static Color BackgroundOverlay => Pick(Light.BackgroundOverlay, Dark.BackgroundOverlay);

    // -- 控件色 --
    public static Color ButtonBorderGray => Pick(Light.ButtonBorderGray, Dark.ButtonBorderGray);
    public static Color ButtonHoverBg => Pick(Light.ButtonHoverBg, Dark.ButtonHoverBg);
    public static Color ButtonPressBg => Pick(Light.ButtonPressBg, Dark.ButtonPressBg);
    public static Color SeparatorGray => Pick(Light.SeparatorGray, Dark.SeparatorGray);
    public static Color DisabledBg => Pick(Light.DisabledBg, Dark.DisabledBg);
    public static Color DisabledText => Pick(Light.DisabledText, Dark.DisabledText);

    // -- 主题归一化映射（令牌层切换的唯一入口，UI 侧不逐处反色） --
    // 系统主题切换后，UI 把控件当前颜色交给本方法，映射到"当前主题下的对值"：
    //   · 控件色 == 浅色令牌值 → 返回对应深色令牌值（深色切换时）
    //   · 控件色 == 深色令牌值 → 返回对应浅色令牌值（切回浅色时）
    // 映射完全在令牌层内部完成，消费处无需关心取的是浅还是深。

    // 映射字典 LightToDark/DarkToLight 在下方 PalettePairs 声明之后构建——
    // 静态字段按声明顺序初始化，PalettePairs 必须先就绪 BuildMap 才能遍历。

    /// <summary>
    /// 把控件背景/表面色归一化到当前主题（幂等：已符合当前主题则原样返回）。
    /// 注意：白色（BackgroundWhite 浅值与 TextOnPrimary 同值）在此视为"表面色"，
    /// 深色下映射为深色表面。白字（按钮文字）请用 <see cref="NormalizeTextToTheme"/>。
    /// </summary>
    public static Color NormalizeToTheme(Color color)
    {
        return IsDark
            ? (LightToDark.TryGetValue(color, out var dark) ? dark : color)
            : (DarkToLight.TryGetValue(color, out var light) ? light : color);
    }

    /// <summary>
    /// 把控件前景文字色归一化到当前主题。白色（TextOnPrimary，蓝底白字）在两种主题下均保持白色，
    /// 避免与 BackgroundWhite 同值歧义导致深色下按钮白字被映射为深灰；
    /// 系统默认前景色（WindowText=Black，仅设 BackColor 未设 ForeColor 的输入框）在深色下转浅色正文。
    /// </summary>
    public static Color NormalizeTextToTheme(Color color)
    {
        if (color == Color.White)
        {
            return color;
        }

        if (IsDark && color == Color.Black)
        {
            return Dark.TextPrimary;
        }

        return NormalizeToTheme(color);
    }

    private static Dictionary<Color, Color> BuildMap(bool isLightToDark)
    {
        var map = new Dictionary<Color, Color>();
        foreach (var (light, dark) in PalettePairs)
        {
            // 浅=深 的令牌（如 TextOnPrimary 白字）无需映射，且避免占用 BackgroundWhite 的 White key
            if (light == dark)
            {
                continue;
            }

            map[isLightToDark ? light : dark] = isLightToDark ? dark : light;
        }

        return map;
    }

    // 浅色组与深色组逐一对（覆盖全部语义色）。注意顺序：
    // 浅色组内存在同值但深色值不同的令牌（BackgroundGray/DisabledBg 均 245，BorderLight/ButtonPressBg 均 225），
    // 后写入者胜出——把"更高频"的令牌放在后面（BackgroundGray 取 (44,44,44)、BorderLight 取 (74,74,74)）。
    private static readonly (Color Light, Color Dark)[] PalettePairs =
    {
        (Light.PrimaryBlue, Dark.PrimaryBlue),
        (Light.PrimaryBlueHover, Dark.PrimaryBlueHover),
        (Light.PrimaryBluePress, Dark.PrimaryBluePress),
        (Light.SuccessGreen, Dark.SuccessGreen),
        (Light.WarningOrange, Dark.WarningOrange),
        (Light.ErrorRed, Dark.ErrorRed),
        (Light.InfoBlue, Dark.InfoBlue),
        (Light.ErrorBgLight, Dark.ErrorBgLight),
        (Light.WarningBgLight, Dark.WarningBgLight),
        (Light.SuccessBgLight, Dark.SuccessBgLight),
        (Light.InfoBgLight, Dark.InfoBgLight),
        (Light.AccentBlue, Dark.AccentBlue),
        (Light.TextPrimary, Dark.TextPrimary),
        (Light.TextSecondary, Dark.TextSecondary),
        (Light.TextMuted, Dark.TextMuted),
        (Light.TextError, Dark.TextError),
        (Light.TextDarkGray, Dark.TextDarkGray),
        (Light.TextOnPrimary, Dark.TextOnPrimary),
        (Light.TextLink, Dark.TextLink),
        (Light.BorderMid, Dark.BorderMid),
        (Light.BorderFocus, Dark.BorderFocus),
        (Light.BackgroundLight, Dark.BackgroundLight),
        (Light.BackgroundWhite, Dark.BackgroundWhite),
        (Light.BackgroundHover, Dark.BackgroundHover),
        (Light.BackgroundOverlay, Dark.BackgroundOverlay),
        (Light.ButtonBorderGray, Dark.ButtonBorderGray),
        (Light.ButtonHoverBg, Dark.ButtonHoverBg),
        (Light.DisabledBg, Dark.DisabledBg),
        (Light.ButtonPressBg, Dark.ButtonPressBg),
        // BorderLight 浅色 (225) 与 ButtonPressBg 浅色同值：后写胜出，取 BorderLight 深色 (74)
        (Light.BorderLight, Dark.BorderLight),
        // BackgroundGray 浅色 (245) 与 DisabledBg 浅色同值：后写胜出，取 BackgroundGray 深色 (44)
        (Light.BackgroundGray, Dark.BackgroundGray),
        (Light.SeparatorGray, Dark.SeparatorGray),
        (Light.DisabledText, Dark.DisabledText),
    };

    private static readonly Dictionary<Color, Color> LightToDark = BuildMap(isLightToDark: true);
    private static readonly Dictionary<Color, Color> DarkToLight = BuildMap(isLightToDark: false);

    // ============================================================
    // 浅色组（浅色主题值，原 DesignTokens 唯一来源的取值）
    // ============================================================
    private static class Light
    {
        // -- 主色调 --
        public static readonly Color PrimaryBlue = Color.FromArgb(0, 120, 212);
        public static readonly Color PrimaryBlueHover = Color.FromArgb(0, 100, 190);
        public static readonly Color PrimaryBluePress = Color.FromArgb(0, 85, 165);

        // -- 状态色 --
        public static readonly Color SuccessGreen = Color.FromArgb(76, 175, 80);
        public static readonly Color WarningOrange = Color.FromArgb(255, 152, 0);
        public static readonly Color ErrorRed = Color.FromArgb(244, 67, 54);
        public static readonly Color InfoBlue = Color.FromArgb(33, 150, 243);

        // -- 状态背景色（浅色） --
        public static readonly Color ErrorBgLight = Color.FromArgb(255, 235, 238);
        public static readonly Color WarningBgLight = Color.FromArgb(255, 243, 224);
        public static readonly Color SuccessBgLight = Color.FromArgb(232, 245, 233);
        public static readonly Color InfoBgLight = Color.FromArgb(227, 242, 253);

        // -- 辅助色 --
        public static readonly Color AccentBlue = Color.FromArgb(33, 150, 243);

        // -- 文字色 --
        public static readonly Color TextPrimary = Color.FromArgb(30, 30, 30);
        public static readonly Color TextSecondary = Color.FromArgb(80, 80, 80);
        public static readonly Color TextMuted = Color.FromArgb(117, 117, 117);
        public static readonly Color TextError = Color.FromArgb(198, 40, 40);
        public static readonly Color TextDarkGray = Color.FromArgb(97, 97, 97);
        public static readonly Color TextOnPrimary = Color.FromArgb(255, 255, 255);
        public static readonly Color TextLink = Color.FromArgb(0, 100, 190);

        // -- 边框与背景 --
        public static readonly Color BorderLight = Color.FromArgb(225, 225, 225);
        public static readonly Color BorderMid = Color.FromArgb(200, 200, 200);
        public static readonly Color BorderFocus = Color.FromArgb(0, 120, 212);
        public static readonly Color BackgroundLight = Color.FromArgb(248, 248, 248);
        public static readonly Color BackgroundWhite = Color.White;
        public static readonly Color BackgroundGray = Color.FromArgb(245, 245, 245);
        public static readonly Color BackgroundHover = Color.FromArgb(235, 241, 248);
        public static readonly Color BackgroundOverlay = Color.FromArgb(0, 0, 0, 96);

        // -- 控件色 --
        public static readonly Color ButtonBorderGray = Color.FromArgb(192, 192, 192);
        public static readonly Color ButtonHoverBg = Color.FromArgb(235, 235, 235);
        public static readonly Color ButtonPressBg = Color.FromArgb(225, 225, 225);
        public static readonly Color SeparatorGray = Color.FromArgb(208, 208, 208);
        public static readonly Color DisabledBg = Color.FromArgb(245, 245, 245);
        public static readonly Color DisabledText = Color.FromArgb(180, 180, 180);
    }

    // ============================================================
    // 深色组（深色主题值：深底浅字，状态色提亮，边框略亮于背景以区分层级）
    // 深色下对比度均达标 WCAG 1.4.3（正文 ≥4.5:1，深底 #1e1e1e 上 TextMuted 约 5.9:1）。
    // ============================================================
    private static class Dark
    {
        // -- 主色调（深底上提亮，保持可辨识） --
        public static readonly Color PrimaryBlue = Color.FromArgb(86, 160, 230);
        public static readonly Color PrimaryBlueHover = Color.FromArgb(110, 176, 240);
        public static readonly Color PrimaryBluePress = Color.FromArgb(66, 138, 210);

        // -- 状态色（提亮，深底上保持语义辨识） --
        public static readonly Color SuccessGreen = Color.FromArgb(110, 190, 115);
        public static readonly Color WarningOrange = Color.FromArgb(255, 172, 40);
        public static readonly Color ErrorRed = Color.FromArgb(250, 96, 84);
        public static readonly Color InfoBlue = Color.FromArgb(90, 170, 250);

        // -- 状态背景色（深色） --
        public static readonly Color ErrorBgLight = Color.FromArgb(66, 34, 36);
        public static readonly Color WarningBgLight = Color.FromArgb(70, 52, 30);
        public static readonly Color SuccessBgLight = Color.FromArgb(36, 62, 42);
        public static readonly Color InfoBgLight = Color.FromArgb(30, 46, 66);

        // -- 辅助色 --
        public static readonly Color AccentBlue = Color.FromArgb(90, 170, 250);

        // -- 文字色（深底浅字） --
        public static readonly Color TextPrimary = Color.FromArgb(226, 226, 226);
        public static readonly Color TextSecondary = Color.FromArgb(190, 190, 190);
        public static readonly Color TextMuted = Color.FromArgb(160, 160, 160);
        public static readonly Color TextError = Color.FromArgb(250, 130, 120);
        public static readonly Color TextDarkGray = Color.FromArgb(170, 170, 170);
        public static readonly Color TextOnPrimary = Color.FromArgb(255, 255, 255);
        public static readonly Color TextLink = Color.FromArgb(110, 176, 240);

        // -- 边框与背景（阴影减弱、以边框区分层级，边框略亮于背景） --
        public static readonly Color BorderLight = Color.FromArgb(74, 74, 74);
        public static readonly Color BorderMid = Color.FromArgb(95, 95, 95);
        public static readonly Color BorderFocus = Color.FromArgb(86, 160, 230);
        public static readonly Color BackgroundLight = Color.FromArgb(36, 36, 36);
        // 深色表面用 (32,32,32) 而非 (30,30,30)：后者与浅色组 TextPrimary 同值，
        // 会破坏 NormalizeToTheme 双向映射幂等（浅色 TextPrimary 被误反映射为 White）
        public static readonly Color BackgroundWhite = Color.FromArgb(32, 32, 32);
        public static readonly Color BackgroundGray = Color.FromArgb(44, 44, 44);
        public static readonly Color BackgroundHover = Color.FromArgb(52, 58, 70);
        public static readonly Color BackgroundOverlay = Color.FromArgb(0, 0, 0, 140);

        // -- 控件色 --
        public static readonly Color ButtonBorderGray = Color.FromArgb(84, 84, 84);
        public static readonly Color ButtonHoverBg = Color.FromArgb(58, 58, 58);
        public static readonly Color ButtonPressBg = Color.FromArgb(48, 48, 48);
        public static readonly Color SeparatorGray = Color.FromArgb(70, 70, 70);
        public static readonly Color DisabledBg = Color.FromArgb(46, 46, 46);
        public static readonly Color DisabledText = Color.FromArgb(110, 110, 110);
    }
}

/// <summary>
/// 应用间距设计令牌。
/// 手工维护（shared-spec.json 不含 designTokens 节，此前“从契约生成”注释为伪称），
/// 为两端 UI（CloudPan.Client.UI / CloudPan.Server.UI）共享的唯一来源。
/// </summary>
public static class CloudPanSpacing
{
    public const int MarginStandard = 28;
    public const int MarginSmall = 16;
    public const int MarginTiny = 8;
    public const int ButtonWidth = 110;
    public const int InputHeight = 34;

    // -- 元素间距（同一分组内相邻元素之间） --
    public const int ElementSpacing = 12;

    // -- 段落间距（垂直方向段落/块之间） --
    public const int ParagraphSpacing = 16;

    // -- 卡片内边距 --
    public const int CardPadding = 20;

    // -- 列表项内边距 --
    public const int ListItemPadding = 10;

    // -- 分组间距（不同分组/区块之间） --
    public const int GroupSpacing = 24;

    // -- 区域间距（页面主要区域之间） --
    public const int SectionSpacing = 32;

    // -- 最小触控/点击尺寸（触摸友好） --
    public const int MinTouchSize = 44;

    // -- 图标尺寸 --
    public const int IconSmall = 12;
    public const int IconMedium = 16;
    public const int IconLarge = 24;
}

/// <summary>
/// 视觉效果设计令牌：阴影、圆角、动画时长。
/// </summary>
public static class CloudPanEffects
{
    // -- 阴影定义结构 --
    /// <summary>
    /// 描述一个阴影层：偏移量、模糊半径与颜色。
    /// WinForms 自身不支持投影，此结构供自定义绘制或后续渲染层使用。
    /// </summary>
    public readonly struct ShadowInfo
    {
        public int OffsetX { get; }
        public int OffsetY { get; }
        public int BlurRadius { get; }
        public Color Color { get; }

        public ShadowInfo(int offsetX, int offsetY, int blurRadius, Color color)
        {
            OffsetX = offsetX;
            OffsetY = offsetY;
            BlurRadius = blurRadius;
            Color = color;
        }
    }

    // -- 阴影预设 --

    /// <summary>小阴影：轻微浮起，用于悬停状态。</summary>
    public static readonly ShadowInfo ShadowSm = new(0, 1, 3, Color.FromArgb(48, 0, 0, 0));

    /// <summary>中阴影：常用于卡片、面板。</summary>
    public static readonly ShadowInfo ShadowMd = new(0, 2, 6, Color.FromArgb(64, 0, 0, 0));

    /// <summary>大阴影：用于对话框、弹出层。</summary>
    public static readonly ShadowInfo ShadowLg = new(0, 4, 12, Color.FromArgb(80, 0, 0, 0));

    /// <summary>超大阴影：用于模态弹窗。</summary>
    public static readonly ShadowInfo ShadowXl = new(0, 6, 20, Color.FromArgb(96, 0, 0, 0));

    /// <summary>多图层阴影（复合阴影），按顺序绘制。</summary>
    public static readonly ShadowInfo[] ShadowElevated =
    {
        new(0, 1, 3, Color.FromArgb(32, 0, 0, 0)),
        new(0, 4, 8, Color.FromArgb(48, 0, 0, 0)),
    };

    // -- 圆角常量 --
    public const int CornerRadiusNone = 0;
    public const int CornerRadiusSm = 4;
    public const int CornerRadiusMd = 8;
    public const int CornerRadiusLg = 12;
    public const int CornerRadiusRound = 20;

    // -- 圆角辅助（返回 CornerRadius 结构体引用值） --
    // WinForms 无原生 CornerRadius 类型，返回 int 让调用方自行构造。

    // -- 动画/过渡时长（毫秒） --
    /// <summary>瞬间（无动画）。</summary>
    public const int DurationInstant = 0;

    /// <summary>极快：微交互、悬停反馈。</summary>
    public const int DurationFast = 100;

    /// <summary>快速：元素出现/消失。</summary>
    public const int DurationNormal = 200;

    /// <summary>中等：面板展开/折叠。</summary>
    public const int DurationSlow = 350;

    /// <summary>慢速：强调动画、页面切换。</summary>
    public const int DurationEmphasis = 500;

    // -- 透明度层级 --
    /// <summary>遮罩层透明度（模态背景）。</summary>
    public const int OverlayAlpha = 96;

    /// <summary>禁用态透明度（百分比）。</summary>
    public const double DisabledOpacity = 0.38;

    /// <summary>悬停态透明度。 </summary>
    public const double HoverOpacity = 0.08;
}

/// <summary>
/// 字体规格设计令牌。
/// 使用 System.Drawing.FontStyle + 字号常量描述字体规格，
/// 调用方使用 <see cref="FontSpec"/> 自行构造 System.Drawing.Font 实例。
/// 默认 UI 字体为 Segoe UI（Windows 系统标准）。
/// </summary>
public static class CloudPanFonts
{
    /// <summary>默认 UI 字体系列名称。</summary>
    public const string FontFamily = "Segoe UI";

    /// <summary>等宽字体系列名称。</summary>
    public const string FontFamilyMono = "Consolas";

    // -- 字号常量 --
    public const float SizeTitle = 20f;
    public const float SizeSubtitle = 14f;

    // -- 正文最小字号约束（WCAG 1.4.3，老人/弱视用户可读性下限） --
    // 正文（Body/阅读文本）字号不得低于此值；BodySmall/Mono 为辅助小字号，
    // Caption 已提至 ≥10 磅。
    /// <summary>正文最小字号（磅）。</summary>
    public const float SizeBodyMin = 14f;
    public const float SizeBody = SizeBodyMin;
    public const float SizeBodySmall = 10f;
    public const float SizeCaption = 10f;
    public const float SizeMono = 10f;

    // -- 字体规格结构 --
    /// <summary>
    /// 字体规格的描述性结构，不持有 GDI 资源。
    /// 调用方可据此构造 System.Drawing.Font：
    /// <code>new Font(spec.Family, spec.Size, (FontStyle)spec.Style, GraphicsUnit.Point)</code>
    /// </summary>
    public readonly struct FontSpec
    {
        /// <summary>字体系列名称（如 "Segoe UI"）。</summary>
        public string Family { get; }

        /// <summary>字号（单位：磅，point）。</summary>
        public float Size { get; }

        /// <summary>字体样式，对应 System.Drawing.FontStyle 枚举值（Regular=0, Bold=1, Italic=2, …）。</summary>
        public int Style { get; }

        public FontSpec(string family, float size, int style)
        {
            Family = family;
            Size = size;
            Style = style;
        }
    }

    // -- 字体规格预设 --

    /// <summary>大标题 — 页面/窗口主标题。</summary>
    public static FontSpec Title => new(FontFamily, SizeTitle, 1); // Bold

    /// <summary>中标题 — 分组/区块标题。</summary>
    public static FontSpec Subtitle => new(FontFamily, SizeSubtitle, 1); // Bold

    /// <summary>正文 — 默认阅读文本。</summary>
    public static FontSpec Body => new(FontFamily, SizeBody, 0); // Regular

    /// <summary>正文（加粗）— 强调文本。</summary>
    public static FontSpec BodyBold => new(FontFamily, SizeBody, 1); // Bold

    /// <summary>小字体 — 辅助信息、状态提示。</summary>
    public static FontSpec BodySmall => new(FontFamily, SizeBodySmall, 0); // Regular

    /// <summary>极小标签 — 徽标、角标、时间戳。</summary>
    public static FontSpec Caption => new(FontFamily, SizeCaption, 0); // Regular

    /// <summary>等宽字体 — 日志、路径、代码片段。</summary>
    public static FontSpec Monospace => new(FontFamilyMono, SizeMono, 0); // Regular

    /// <summary>按钮默认字体。</summary>
    public static FontSpec Button => new(FontFamily, SizeBody, 0); // Regular
}
