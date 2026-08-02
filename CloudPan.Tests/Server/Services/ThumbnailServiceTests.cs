using CloudPan.Contract;
using CloudPan.Infrastructure.Storage;
using CloudPan.Server.Core;
using SkiaSharp;
using Xunit;

namespace CloudPan.Tests.Server.Services;

/// <summary>
/// ThumbnailService 单元测试——缩略图生成、类型判定、缓存 key 失效与并发生成限流（脱离 ASP.NET，直接注入领域服务）。
/// </summary>
public class ThumbnailServiceTests : Infrastructure.TestBase
{
    private async Task<string> WriteTestImageAsync(string fileName, int width = 100, int height = 80)
    {
        // 用 SkiaSharp 生成一张红色 PNG
        using SKBitmap bmp = new SKBitmap(width, height);
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

    [Fact]
    public async Task GetThumbnail_文件更新后_缓存key变化()
    {
        // 验收场景：文件更新（索引版本/hash 变化）后旧缩略图失效，缓存 key 变化
        await WriteTestImageAsync("photo.png");
        var index = new FileIndexService(CreateServerDbFactory());
        var svc = new ThumbnailService(new FileStorageService(TempDir), index);

        // v1：先生成缩略图
        await index.UpsertFileAsync("/photo.png", FileType.File, "hash-v1", 100,
            DateTime.UtcNow.ToString("O"), 1);
        var first = await svc.GetThumbnailAsync("/photo.png", 200);
        Assert.True(first.Success);

        // 文件更新：版本号提升 + hash 变化（磁盘文件未变，隔离出 version/hash 为唯一差异）
        await index.UpsertFileAsync("/photo.png", FileType.File, "hash-v2", 120,
            DateTime.UtcNow.ToString("O"), 2);
        var second = await svc.GetThumbnailAsync("/photo.png", 200);

        Assert.True(second.Success);
        Assert.NotEqual(first.CachePath, second.CachePath);
    }

    [Fact]
    public async Task GetThumbnail_未入索引文件重写_缓存key变化()
    {
        // 未入索引的文件：磁盘元数据指纹兜底，内容更新后同样使旧缓存失效
        await WriteTestImageAsync("rewrite.png", 100, 80);
        var svc = new ThumbnailService(new FileStorageService(TempDir));

        var first = await svc.GetThumbnailAsync("/rewrite.png", 200);
        Assert.True(first.Success);

        // 重写为不同内容（不同尺寸，保证长度变化）
        await WriteTestImageAsync("rewrite.png", 50, 40);
        var second = await svc.GetThumbnailAsync("/rewrite.png", 200);

        Assert.True(second.Success);
        Assert.NotEqual(first.CachePath, second.CachePath);
    }

    [Fact]
    public async Task GetThumbnail_同一图片并发请求_共享同一缓存()
    {
        // 并发门内双检：同一缩略图并发请求只产生一个缓存文件
        await WriteTestImageAsync("shared.png");
        var svc = new ThumbnailService(new FileStorageService(TempDir));

        var tasks = Enumerable.Range(0, 8).Select(_ => svc.GetThumbnailAsync("/shared.png", 100));
        var results = await Task.WhenAll(tasks);

        Assert.All(results, r => Assert.True(r.Success));
        Assert.Single(results.Select(r => r.CachePath).Distinct());
    }

    [Fact]
    public async Task GetThumbnail_多图并发_全部生成成功()
    {
        // 受限并发队列：多图并发经并发门限流后全部生成成功、缓存互不串扰
        var svc = new ThumbnailService(new FileStorageService(TempDir));

        var tasks = Enumerable.Range(1, 20).Select(async i =>
        {
            await WriteTestImageAsync($"img_{i:D2}.png");
            return await svc.GetThumbnailAsync($"/img_{i:D2}.png", 100);
        });
        var results = await Task.WhenAll(tasks);

        Assert.All(results, r => Assert.True(r.Success));
        Assert.Equal(20, results.Select(r => r.CachePath).Distinct().Count());
    }
}
