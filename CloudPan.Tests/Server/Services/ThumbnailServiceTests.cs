using CloudPan.Server.Services;
using CloudPan.Shared;
using SkiaSharp;
using Xunit;

namespace CloudPan.Tests.Server.Services;

/// <summary>
/// ThumbnailService 单元测试——缩略图生成与类型判定（脱离 ASP.NET，直接注入领域服务）。
/// </summary>
public class ThumbnailServiceTests : Infrastructure.TestBase
{
    private async Task<string> WriteTestImageAsync(string fileName)
    {
        // 用 SkiaSharp 生成一张 100x80 红色 PNG
        using SKBitmap bmp = new SKBitmap(100, 80);
        using SKCanvas canvas = new SKCanvas(bmp);
        canvas.Clear(SKColors.Red);
        using SKImage img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        string abs = Path.Combine(TempDir, fileName);
        await File.WriteAllBytesAsync(abs, data.ToArray());
        return abs;
    }

    [Fact]
    public async Task GetThumbnail_图片_生成缓存文件()
    {
        await WriteTestImageAsync("photo.png");
        var svc = new ThumbnailService(new FileStorageService(TempDir));

        var result = await svc.GetThumbnailAsync("/photo.png", 200);

        Assert.True(result.Success);
        Assert.NotNull(result.CachePath);
        Assert.True(File.Exists(result.CachePath));
        Assert.EndsWith(".jpg", result.CachePath);
    }

    [Fact]
    public async Task GetThumbnail_非图片_返回错误()
    {
        await File.WriteAllTextAsync(Path.Combine(TempDir, "doc.txt"), "not an image");
        var svc = new ThumbnailService(new FileStorageService(TempDir));

        var result = await svc.GetThumbnailAsync("/doc.txt", 200);

        Assert.False(result.Success);
        Assert.Equal(HttpErrorCode.NOT_FOUND.Code, result.Error!.Code.Code);
    }

    [Fact]
    public async Task GetThumbnail_不存在的路径_返回错误()
    {
        var svc = new ThumbnailService(new FileStorageService(TempDir));

        var result = await svc.GetThumbnailAsync("/ghost.png", 200);

        Assert.False(result.Success);
        Assert.Equal(HttpErrorCode.NOT_FOUND.Code, result.Error!.Code.Code);
    }

    [Fact]
    public async Task GetThumbnail_缓存命中_返回同一路径()
    {
        await WriteTestImageAsync("photo.png");
        var svc = new ThumbnailService(new FileStorageService(TempDir));

        var first = await svc.GetThumbnailAsync("/photo.png", 200);
        var second = await svc.GetThumbnailAsync("/photo.png", 200);

        Assert.True(first.Success);
        Assert.True(second.Success);
        Assert.Equal(first.CachePath, second.CachePath);
    }
}
