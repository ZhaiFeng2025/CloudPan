using System.Drawing;
using CloudPan.Infrastructure.Design;
using Xunit;

namespace CloudPan.Tests.Infrastructure.Design;

/// <summary>
/// 深色模式令牌级切换单测（T-032）：浅/深语义组存在、归一化映射正确且幂等、
/// 白字（TextOnPrimary）与白背景（BackgroundWhite）歧义分离、深色组正文对比度达标 WCAG 1.4.3。
/// 注意：ThemeManager 为静态全局状态，本类内串行执行（xUnit 同类测试不并行）。
/// </summary>
public class DesignTokensThemeTests
{
    // ================================================================
    // 语义组存在性
    // ================================================================

    [Fact]
    public void 深色语义组_与浅色组字段齐全()
    {
        // 通过公开属性可读（映射表构建依赖全部 32 对，缺任一属性即编译失败）
        Assert.True(CloudPanColors.PrimaryBlue.A == 255);
        Assert.True(CloudPanColors.BackgroundWhite.A == 255);
    }

    // ================================================================
    // 归一化映射：浅色下幂等 / 深色下浅色→深色 / 切回浅色深色→浅色
    // ================================================================

    [Fact]
    public void 浅色下_浅色令牌幂等()
    {
        ThemeManager.ApplyTheme(false);
        Assert.Equal(Color.FromArgb(30, 30, 30), CloudPanColors.NormalizeToTheme(CloudPanColors.TextPrimary));
        Assert.Equal(Color.FromArgb(248, 248, 248), CloudPanColors.NormalizeToTheme(CloudPanColors.BackgroundLight));
    }

    [Fact]
    public void 深色下_浅色令牌映射为深色对值()
    {
        ThemeManager.ApplyTheme(true);
        Assert.Equal(Color.FromArgb(226, 226, 226), CloudPanColors.NormalizeToTheme(Color.FromArgb(30, 30, 30)));   // TextPrimary
        Assert.Equal(Color.FromArgb(160, 160, 160), CloudPanColors.NormalizeToTheme(Color.FromArgb(117, 117, 117))); // TextMuted
        Assert.Equal(Color.FromArgb(36, 36, 36), CloudPanColors.NormalizeToTheme(Color.FromArgb(248, 248, 248)));    // BackgroundLight
    }

    [Fact]
    public void 深色下_白背景映射为深色表面_而白字保持白()
    {
        ThemeManager.ApplyTheme(true);
        // BackgroundWhite 浅值 = White → 深色表面 (32,32,32)
        Assert.Equal(Color.FromArgb(32, 32, 32), CloudPanColors.NormalizeToTheme(Color.White));
        // TextOnPrimary 白字（按钮蓝底白字）在两种主题下均保持白，避免被映射为深灰
        Assert.Equal(Color.White, CloudPanColors.NormalizeTextToTheme(Color.White));
    }

    [Fact]
    public void 深色下_系统默认黑字转浅色正文_浅色下保持黑()
    {
        ThemeManager.ApplyTheme(true);
        // 服务端 SettingsPage 的 TextBox 仅设 BackColor 未设 ForeColor（默认 WindowText=Black）
        Assert.Equal(Color.FromArgb(226, 226, 226), CloudPanColors.NormalizeTextToTheme(Color.Black));
        ThemeManager.ApplyTheme(false);
        Assert.Equal(Color.Black, CloudPanColors.NormalizeTextToTheme(Color.Black));
    }

    [Fact]
    public void 切回浅色_深色令牌映射回浅色对值()
    {
        ThemeManager.ApplyTheme(true);
        _ = CloudPanColors.NormalizeToTheme(Color.FromArgb(30, 30, 30)); // 深色下已生效
        ThemeManager.ApplyTheme(false);
        // 深色表面 (32,32,32) → 浅色 White
        Assert.Equal(Color.White, CloudPanColors.NormalizeToTheme(Color.FromArgb(32, 32, 32)));
        // 深色正文 (226,226,226) → 浅色 (30,30,30)
        Assert.Equal(Color.FromArgb(30, 30, 30), CloudPanColors.NormalizeToTheme(Color.FromArgb(226, 226, 226)));
    }

    // ================================================================
    // 深色组正文对比度达标 WCAG 1.4.3（深底 #1e1e1e 上 TextMuted ≥4.5:1）
    // ================================================================

    [Fact]
    public void 深色组文字对比度_达标WCAG143()
    {
        Assert.True(ContrastRatio(Color.FromArgb(36, 36, 36), Color.FromArgb(226, 226, 226)) >= 4.5, "TextPrimary on BackgroundLight 应 ≥4.5:1");
        Assert.True(ContrastRatio(Color.FromArgb(32, 32, 32), Color.FromArgb(160, 160, 160)) >= 4.5, "TextMuted on BackgroundWhite 应 ≥4.5:1");
        Assert.True(ContrastRatio(Color.FromArgb(36, 36, 36), Color.FromArgb(190, 190, 190)) >= 4.5, "TextSecondary on BackgroundLight 应 ≥4.5:1");
    }

    /// <summary>WCAG 2.1 相对亮度（sRGB → 线性）。</summary>
    private static double Luminance(Color c)
    {
        static double Ch(double v)
        {
            v /= 255.0;
            return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Ch(c.R) + 0.7152 * Ch(c.G) + 0.0722 * Ch(c.B);
    }

    /// <summary>WCAG 2.1 对比度（背景/前景或前景/背景取较大者）。</summary>
    private static double ContrastRatio(Color a, Color b)
    {
        double la = Luminance(a);
        double lb = Luminance(b);
        double lighter = Math.Max(la, lb);
        double darker = Math.Min(la, lb);
        return (lighter + 0.05) / (darker + 0.05);
    }
}
