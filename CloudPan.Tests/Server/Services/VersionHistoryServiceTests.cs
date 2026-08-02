using System.Text;
using CloudPan.Infrastructure.Storage;
using CloudPan.Server.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CloudPan.Tests.Server.Services;

/// <summary>
/// VersionHistoryService 单元测试——历史版本列表与回滚（脱离 ASP.NET，直接注入领域服务）。
/// 通过 UploadService 建立多版本后验证列表与回滚内容。
/// </summary>
public class VersionHistoryServiceTests : Infrastructure.TestBase
{
    private Task<(VersionHistoryService versions, FileStorageService storage, UploadService upload)> CreateServiceAsync()
    {
        var dbFactory = CreateServerDbFactory();
        var storage = new FileStorageService(TempDir);
        var version = new VersionService(dbFactory);
        var upload = new UploadService(storage, version, dbFactory, NullLogger<UploadService>.Instance);
        var syncLog = new SyncLogService(dbFactory, NullLogger<SyncLogService>.Instance);
        var versions = new VersionHistoryService(dbFactory, storage, new FileIndexService(dbFactory), version, syncLog);
        return Task.FromResult((versions, storage, upload));
    }

    private static Stream ToStream(string text) => new MemoryStream(Encoding.UTF8.GetBytes(text));

    [Fact]
    public async Task GetVersions_上传两次_包含旧版本记录()
    {
        var (versions, _, upload) = await CreateServiceAsync();
        await using var s1 = ToStream("version 1");
        await upload.UploadAsync("/file.txt", s1, 9, DateTime.UtcNow.ToString("O"), "server");
        await using var s2 = ToStream("version 2 longer");
        await upload.UploadAsync("/file.txt", s2, 16, DateTime.UtcNow.ToString("O"), "server");

        var list = await versions.GetVersionsAsync("/file.txt", 10);

        // 第二次上传存档了 v1，故历史至少包含一条记录
        var record = Assert.Single(list);
        Assert.Equal(9, record.Size); // 存档的是旧内容长度
    }

    [Fact]
    public async Task Restore_回滚到旧版本_内容变为目标版本()
    {
        var (versions, storage, upload) = await CreateServiceAsync();
        await using var s1 = ToStream("original");
        await upload.UploadAsync("/file.txt", s1, 8, DateTime.UtcNow.ToString("O"), "server");
        await using var s2 = ToStream("newer content");
        await upload.UploadAsync("/file.txt", s2, 13, DateTime.UtcNow.ToString("O"), "server");

        // 当前内容为 v2
        Assert.Equal("newer content", await File.ReadAllTextAsync(Path.Combine(TempDir, "file.txt")));

        var list = await versions.GetVersionsAsync("/file.txt", 10);
        int v1 = list.Single().Version;

        var result = await versions.RestoreAsync("/file.txt", v1, "server");

        Assert.True(result.Success);
        Assert.Equal(v1, result.RestoredFromVersion);
        // 回滚后目标文件内容 = 旧版本内容
        Assert.Equal("original", await File.ReadAllTextAsync(Path.Combine(TempDir, "file.txt")));
    }

    [Fact]
    public async Task Restore_不存在的版本_返回错误()
    {
        var (versions, _, upload) = await CreateServiceAsync();
        await using var s1 = ToStream("only");
        await upload.UploadAsync("/file.txt", s1, 4, DateTime.UtcNow.ToString("O"), "server");

        var result = await versions.RestoreAsync("/file.txt", 999, "server");

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
    }
}
