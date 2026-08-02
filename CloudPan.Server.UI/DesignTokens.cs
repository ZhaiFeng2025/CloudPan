using System.Drawing;

namespace CloudPan.Shared;

/// <summary>
/// 应用调色板设计令牌。
/// 所有颜色值从 shared-spec.json designTokens.theme 契约生成。
/// </summary>
public static class CloudPanColors
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

/// <summary>
/// 应用间距设计令牌。
/// 所有间距值从 shared-spec.json designTokens.spacing 契约生成。
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
