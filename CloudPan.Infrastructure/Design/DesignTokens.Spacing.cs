namespace CloudPan.Infrastructure.Design;

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
