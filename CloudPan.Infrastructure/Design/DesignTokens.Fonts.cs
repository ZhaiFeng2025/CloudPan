namespace CloudPan.Infrastructure.Design;

/// <summary>
/// 字体规格设计令牌：字体系列名与字号常量（均保留有真实消费者的令牌）。
/// 默认 UI 字体为 Segoe UI（Windows 系统标准），等宽为 Consolas；调用方用
/// <c>new Font(FontFamily, SizeBody)</c> 自行构造 System.Drawing.Font。
/// FontSpec 结构及其 8 个预设（Title/Subtitle/Body/BodyBold/BodySmall/Caption/Monospace/Button）
/// 全库 0 引用，已在 T-080 删除。
/// </summary>
public static class CloudPanFonts
{
    /// <summary>默认 UI 字体系列名称。</summary>
    public const string FontFamily = "Segoe UI";

    /// <summary>等宽字体系列名称。</summary>
    public const string FontFamilyMono = "Consolas";

    // -- 字号常量（均有消费者，代表性消费点见行内注释） --
    // SizeBody：FileBrowserView:167/291/314/329、SettingsPage:131/142/268、ServerWindow:41、MainWindow.* 等数十处；
    // SizeSubtitle：SettingsPage:252、ServerWindow.Events:144、SetupForm.Layout:322；
    // SizeCaption：ServerInstaller:198/220、ServerWindow:187、SetupForm.Layout:294、MainWindow.Share:144；
    // SizeMono：ServerInstaller.Install:246、SettingsPage:177、ServerWindow:174、MainWindow.Layout:219；
    // SizeBodySmall：SettingsPage:108/225/284、ServerWindow:164、MainWindow.Layout:73/98。
    public const float SizeSubtitle = 14f;

    // -- 正文最小字号约束（WCAG 1.4.3，老人/弱视用户可读性下限） --
    // 正文（Body/阅读文本）字号不得低于此值；BodySmall/Mono 为辅助小字号，Caption 已提至 ≥10 磅。
    /// <summary>正文最小字号（磅）。</summary>
    public const float SizeBodyMin = 14f;
    public const float SizeBody = SizeBodyMin;
    public const float SizeBodySmall = 10f;
    public const float SizeCaption = 10f;
    public const float SizeMono = 10f;
}
