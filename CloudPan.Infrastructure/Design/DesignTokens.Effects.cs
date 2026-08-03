using System.Drawing;

namespace CloudPan.Infrastructure.Design;

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
