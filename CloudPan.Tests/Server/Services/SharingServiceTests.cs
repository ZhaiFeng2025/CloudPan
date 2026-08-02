using CloudPan.Contract;
using CloudPan.Infrastructure.Storage;
using CloudPan.Server.Core;
using Xunit;

namespace CloudPan.Tests.Server.Services;

/// <summary>
/// SharingService 单元测试——分享创建/撤销/访问校验/下载（脱离 ASP.NET，直接注入领域服务）。
/// </summary>
public class SharingServiceTests : Infrastructure.TestBase
{
    private async Task<(SharingService svc, FileStorageService storage)> CreateServiceAsync()
    {
        var dbFactory = CreateServerDbFactory();
        var storage = new FileStorageService(TempDir);

        // 建立索引条目（分享创建要求文件存在于索引）
        var index = new FileIndexService(dbFactory);
        string abs = Path.Combine(TempDir, "test.txt");
        await File.WriteAllTextAsync(abs, "hello");
        await index.UpsertFileAsync("/test.txt", FileType.File, "hash", 5, DateTime.UtcNow.ToString("O"), 1);

        return (new SharingService(dbFactory, storage, index), storage);
    }

    [Fact]
    public async Task CreateShare_存在文件_返回分享ID()
    {
        var (svc, _) = await CreateServiceAsync();

        var result = await svc.CreateShareAsync("/test.txt", null, null, null, "dev-1");

        Assert.True(result.Success);
        Assert.NotNull(result.ShareId);
        Assert.Equal(32, result.ShareId!.Length); // 32 hex
    }

    [Fact]
    public async Task CreateShare_不存在文件_返回错误()
    {
        var (svc, _) = await CreateServiceAsync();

        var result = await svc.CreateShareAsync("/missing.txt", null, null, null, "dev-1");

        Assert.False(result.Success);
        Assert.Equal(HttpErrorCode.NOT_FOUND.Code, result.Error!.Code.Code);
    }

    [Fact]
    public async Task RevokeShare_撤销后_查询不存在()
    {
        var (svc, _) = await CreateServiceAsync();
        var created = await svc.CreateShareAsync("/test.txt", null, null, null, "dev-1");

        var revoked = await svc.RevokeShareAsync(created.ShareId!);
        Assert.True(revoked.Success);

        var info = await svc.GetShareInfoAsync(created.ShareId!);
        Assert.False(info.Success);
    }

    [Fact]
    public async Task GetShareInfo_过期分享_标记Expired()
    {
        var (svc, _) = await CreateServiceAsync();
        var created = await svc.CreateShareAsync("/test.txt", null, DateTime.UtcNow.AddHours(-1).ToString("O"), null, "dev-1");

        var info = await svc.GetShareInfoAsync(created.ShareId!);

        Assert.True(info.Success);
        Assert.True(info.Expired);
    }

    [Fact]
    public async Task GetShareInfo_下载次数用尽_标记LimitReached()
    {
        var (svc, _) = await CreateServiceAsync();
        var created = await svc.CreateShareAsync("/test.txt", null, null, 1, "dev-1");

        // 用掉唯一一次下载
        var dl = await svc.PrepareDownloadAsync(created.ShareId!, null);
        Assert.True(dl.Success);
        dl.Content?.Dispose();

        var info = await svc.GetShareInfoAsync(created.ShareId!);
        Assert.True(info.DownloadLimitReached);
    }

    [Fact]
    public async Task PrepareDownload_无密码分享_返回文件流()
    {
        var (svc, _) = await CreateServiceAsync();
        var created = await svc.CreateShareAsync("/test.txt", null, null, null, "dev-1");

        var result = await svc.PrepareDownloadAsync(created.ShareId!, null);

        Assert.True(result.Success);
        Assert.NotNull(result.Content);
        using var reader = new StreamReader(result.Content!);
        Assert.Equal("hello", await reader.ReadToEndAsync());
        Assert.Equal("test.txt", result.FileName);
    }

    [Fact]
    public async Task ShareDownload_过期分享_返回错误()
    {
        var (svc, _) = await CreateServiceAsync();
        var created = await svc.CreateShareAsync("/test.txt", null, DateTime.UtcNow.AddHours(-1).ToString("O"), null, "dev-1");

        var result = await svc.PrepareDownloadAsync(created.ShareId!, null);

        Assert.False(result.Success);
        Assert.Null(result.Content);
        Assert.Equal(HttpErrorCode.BAD_REQUEST.Code, result.Error!.Code.Code);
        Assert.Equal("分享链接已过期", result.Error!.Message);
    }

    [Fact]
    public async Task ShareDownload_未过期分享_正常下载()
    {
        var (svc, _) = await CreateServiceAsync();
        var created = await svc.CreateShareAsync("/test.txt", null, DateTime.UtcNow.AddHours(1).ToString("O"), null, "dev-1");

        var result = await svc.PrepareDownloadAsync(created.ShareId!, null);

        Assert.True(result.Success);
        Assert.NotNull(result.Content);
        using var reader = new StreamReader(result.Content!);
        Assert.Equal("hello", await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task PrepareDownload_密码错误_返回错误()
    {
        var (svc, _) = await CreateServiceAsync();
        var created = await svc.CreateShareAsync("/test.txt", "secret", null, null, "dev-1");

        var result = await svc.PrepareDownloadAsync(created.ShareId!, "wrong");

        Assert.False(result.Success);
        Assert.Equal(HttpErrorCode.UNAUTHORIZED.Code, result.Error!.Code.Code);
    }

    [Fact]
    public async Task PrepareDownload_密码正确_返回文件流()
    {
        var (svc, _) = await CreateServiceAsync();
        var created = await svc.CreateShareAsync("/test.txt", "secret", null, null, "dev-1");

        var result = await svc.PrepareDownloadAsync(created.ShareId!, "secret");

        Assert.True(result.Success);
        Assert.NotNull(result.Content);
        result.Content?.Dispose();
    }

    [Fact]
    public async Task PrepareDownload_下载次数超限_返回错误()
    {
        var (svc, _) = await CreateServiceAsync();
        var created = await svc.CreateShareAsync("/test.txt", null, null, 1, "dev-1");

        var first = await svc.PrepareDownloadAsync(created.ShareId!, null);
        Assert.True(first.Success);
        first.Content?.Dispose();

        var second = await svc.PrepareDownloadAsync(created.ShareId!, null);
        Assert.False(second.Success);
        Assert.Equal(HttpErrorCode.BAD_REQUEST.Code, second.Error!.Code.Code);
    }
}
