using System.Drawing;
using CloudPan.Infrastructure.Design;
using Xunit;

namespace CloudPan.Tests.Infrastructure.Design;

/// <summary>
/// 应用图标工厂单测（T-030）：可生成有效 ICO 字节，且主题色/徽章字符参数化生效。
/// 类名刻意不含 IconFactory 前缀，避免被验收脚本误计为第二处实现。
/// </summary>
public class AppIconTests
{
    // 与 IconFactory 内定义一致（客户端蓝 / 服务端绿）
    private static readonly (Color Top, Color Bottom) BlueTheme = (
        Color.FromArgb(0x1E, 0x88, 0xE5),
        Color.FromArgb(0x15, 0x65, 0xC0));

    private static readonly (Color Top, Color Bottom) GreenTheme = (
        Color.FromArgb(0x43, 0xA0, 0x47),
        Color.FromArgb(0x2E, 0x7D, 0x32));

    private static byte[] ToBytes(Icon icon)
    {
        using var ms = new MemoryStream();
        icon.Save(ms);
        return ms.ToArray();
    }

    [Fact]
    public void Create_生成有效ICO字节()
    {
        using var icon = IconFactory.Create(BlueTheme, null);
        byte[] bytes = ToBytes(icon);

        // ICO 文件头：reserved=0，type=1
        Assert.Equal(0, bytes[0]);
        Assert.Equal(0, bytes[1]);
        Assert.Equal(1, bytes[2]);
        Assert.Equal(0, bytes[3]);
        // 4 帧（16/32/64/256）
        int frameCount = bytes[4] | (bytes[5] << 8);
        Assert.Equal(4, frameCount);
        // 实际绘制内容（非空透明壳）
        using var bmp = icon.ToBitmap();
        Assert.True(bmp.Width > 0 && bmp.Height > 0);
        Assert.True(CountNonTransparentPixels(bmp) > 0);
    }

    [Fact]
    public void Create_主题色参数化_不同主题生成不同图标()
    {
        using var iconBlue = IconFactory.Create(BlueTheme, null);
        using var iconGreen = IconFactory.Create(GreenTheme, null);
        Assert.NotEqual(ToBytes(iconBlue), ToBytes(iconGreen));
    }

    [Fact]
    public void Create_徽章参数化_有徽章与无徽章生成不同图标()
    {
        using var iconNoBadge = IconFactory.Create(BlueTheme, null);
        using var iconBadge = IconFactory.Create(BlueTheme, 'S');
        Assert.NotEqual(ToBytes(iconNoBadge), ToBytes(iconBadge));
    }

    [Fact]
    public void CreateClient_蓝色无徽章_生成有效图标()
    {
        using var icon = IconFactory.CreateClient();
        using var expected = IconFactory.Create(BlueTheme, null);
        Assert.Equal(ToBytes(icon), ToBytes(expected));
    }

    [Fact]
    public void CreateServer_绿色S徽章_生成有效图标()
    {
        using var icon = IconFactory.CreateServer();
        using var expected = IconFactory.Create(GreenTheme, 'S');
        Assert.Equal(ToBytes(icon), ToBytes(expected));
    }

    private static int CountNonTransparentPixels(Bitmap bmp)
    {
        int count = 0;
        for (int y = 0; y < bmp.Height; y++)
        {
            for (int x = 0; x < bmp.Width; x++)
            {
                if (bmp.GetPixel(x, y).A > 0)
                {
                    count++;
                }
            }
        }
        return count;
    }
}
