using CloudPan.Infrastructure.Imaging;
using SkiaSharp;
using Xunit;

namespace CloudPan.Tests.Infrastructure;

/// <summary>
/// Windows 系统 WIC 解码器测试——验证 WIC COM 互操作正确（HEIC 与 PNG 走同一 WIC 解码后端，
/// Windows 10 1809+ 系统自带 HEIF 解码器）。仅 Windows 环境有效（产品 Windows-only）。
/// </summary>
public class WicImageDecoderTests : TestBase
{
    [Fact]
    public void TryDecode_解码PNG_返回BGRA像素()
    {
        // 用 SkiaSharp 生成一张 60x40 红色 PNG 作为 WIC 解码输入
        string pngPath = Path.Combine(TempDir, "pic.png");
        using (SKBitmap bmp = new(60, 40))
        using (SKCanvas canvas = new(bmp))
        {
            canvas.Clear(SKColors.Red);
            using SKImage img = SKImage.FromBitmap(bmp);
            using var data = img.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(pngPath, data.ToArray());
        }

        var decoder = new WicImageDecoder();
        DecodedBitmap? result = decoder.TryDecode(pngPath);

        Assert.NotNull(result);
        Assert.Equal(60, result.Width);
        Assert.Equal(40, result.Height);
        Assert.Equal(60 * 40 * 4, result.BgraPixels.Length);
    }

    [Fact]
    public void TryDecode_非法文件_返回null()
    {
        string path = Path.Combine(TempDir, "bad.png");
        File.WriteAllText(path, "not an image");

        var decoder = new WicImageDecoder();
        DecodedBitmap? result = decoder.TryDecode(path);

        Assert.Null(result);
    }

    [Fact]
    public void TryDecode_空路径_返回null()
    {
        var decoder = new WicImageDecoder();
        Assert.Null(decoder.TryDecode(""));
        Assert.Null(decoder.TryDecode(null!));
    }
}
