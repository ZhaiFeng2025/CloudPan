namespace CloudPan.Infrastructure.Design;

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
