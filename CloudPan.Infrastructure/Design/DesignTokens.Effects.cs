namespace CloudPan.Infrastructure.Design;

/// <summary>
/// 视觉效果设计令牌：圆角、动画时长。
/// 仅保留有真实消费者的令牌；0 引用令牌（阴影 ShadowSm/Md/Lg/Xl/Elevated、CornerRadiusNone/Sm/Lg/Round、
/// DurationInstant/Fast/Slow/Emphasis、Opacity 与 ShadowInfo 结构）已在 T-080 删除。
/// </summary>
public static class CloudPanEffects
{
    // -- 圆角常量 --

    /// <summary>中圆角：卡片、面板。</summary>
    // 消费点：CloudPan.Server.UI/ServerWindow.Events.cs:121,127（SetRoundedRegion 圆角面板）。
    public const int CornerRadiusMd = 8;

    // -- 动画/过渡时长（毫秒） --

    /// <summary>快速：元素出现/消失。</summary>
    // 消费点：CloudPan.Server.UI/ServerInstaller.Steps.cs:144（安装向导步骤进度条定时器 Interval）。
    public const int DurationNormal = 200;
}
