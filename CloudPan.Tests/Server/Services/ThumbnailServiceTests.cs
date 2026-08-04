using CloudPan.Contract;
using CloudPan.Infrastructure.Imaging;
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
    public async Task ReclaimExpiredThumbnails_删除过期缓存_保留新缓存()
    {
        // T-088：缩略图缓存按创建时间/LRU 定期回收——过期（LastWriteTime 早于 cutoff）的缓存被删除，
        // 新缓存保留（重建成本低，下次请求按内容指纹重新生成）
        await WriteTestImageAsync("photo.png");
        await WriteTestImageAsync("photo2.png");
        var svc = new ThumbnailService(new FileStorageService(TempDir));

        Assert.True((await svc.GetThumbnailAsync("/photo.png", 200)).Success);
        Assert.True((await svc.GetThumbnailAsync("/photo2.png", 200)).Success);

        string thumbsDir = Path.Combine(TempDir, ".cloudpan", ".thumbnails");
        var files = Directory.GetFiles(thumbsDir, "*.jpg");
        Assert.Equal(2, files.Length);

        // 把其中一个缓存文件时间拨老（模拟长期未使用），另一个保持新
        File.SetLastWriteTimeUtc(files[0], DateTime.UtcNow.AddDays(-40));

        int reclaimed = await svc.ReclaimExpiredThumbnailsAsync(DateTime.UtcNow.AddDays(-30));
        Assert.Equal(1, reclaimed);
        Assert.Single(Directory.GetFiles(thumbsDir, "*.jpg"));
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

    // ---- T-102：HEIC/HEIF 缩略图（SupportedExts 放行 + 解码器抽象）----

    /// <summary>伪造解码器：模拟系统 WIC 解码 HEIC 成功返回红色 BGRA 像素，隔离 Core 集成路径。</summary>
    private sealed class FakeImageDecoder : IImageDecoder
    {
        public DecodedBitmap? Result { get; set; }
        public string? LastPath { get; private set; }

        public DecodedBitmap? TryDecode(string absolutePath)
        {
            LastPath = absolutePath;
            return Result;
        }
    }

    [Fact]
    public async Task GetThumbnail_heic_经解码器抽象_生成缩略图()
    {
        // T-102 验收：.heic 进入 SupportedExts（不再 NOT_FOUND 为不支持类型），解码经 IImageDecoder 抽象生成缩略图
        await File.WriteAllBytesAsync(Path.Combine(TempDir, "photo.heic"), new byte[] { 0x00, 0x01, 0x02 });

        // 伪造 WIC 解码结果：100x80 红色 BGRA8
        byte[] pixels = new byte[100 * 80 * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 0; pixels[i + 1] = 0; pixels[i + 2] = 255; pixels[i + 3] = 255;
        }
        var fake = new FakeImageDecoder { Result = new DecodedBitmap(100, 80, pixels) };
        var svc = new ThumbnailService(new FileStorageService(TempDir), null, fake);

        var result = await svc.GetThumbnailAsync("/photo.heic", 200);

        Assert.True(result.Success);
        Assert.NotNull(result.CachePath);
        Assert.True(File.Exists(result.CachePath));
        Assert.EndsWith(".jpg", result.CachePath);
        Assert.EndsWith("photo.heic", fake.LastPath);
    }

    [Fact]
    public async Task GetThumbnail_heif_经解码器抽象_生成缩略图()
    {
        // .heif 扩展名同样放行（heic/heif 同源容器）
        await File.WriteAllBytesAsync(Path.Combine(TempDir, "photo.heif"), new byte[] { 0x00 });
        var fake = new FakeImageDecoder
        {
            Result = new DecodedBitmap(50, 40, new byte[50 * 40 * 4])
        };
        var svc = new ThumbnailService(new FileStorageService(TempDir), null, fake);

        var result = await svc.GetThumbnailAsync("/photo.heif", 100);

        Assert.True(result.Success);
        Assert.True(File.Exists(result.CachePath));
    }

    [Fact]
    public async Task GetThumbnail_heic_无解码器_进入解码路径而非不支持类型()
    {
        // 类型判定已放行 heic：即使解码失败，错误是"无法解码"而不是"不支持的文件类型"（NOT_FOUND 语义不同）
        await File.WriteAllTextAsync(Path.Combine(TempDir, "photo.heic"), "not an image");
        var svc = new ThumbnailService(new FileStorageService(TempDir));

        var result = await svc.GetThumbnailAsync("/photo.heic", 200);

        Assert.False(result.Success);
        Assert.Equal(HttpErrorCode.NOT_FOUND.Code, result.Error!.Code.Code);
        Assert.Contains("无法生成缩略图", result.Error.Message);
        Assert.DoesNotContain("不是支持的图片类型", result.Error.Message);
    }
}
