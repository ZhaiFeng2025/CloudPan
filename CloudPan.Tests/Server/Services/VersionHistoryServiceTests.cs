using System.Text;
using CloudPan.Infrastructure.Persistence;
using CloudPan.Infrastructure.Storage;
using CloudPan.Server.Core;
using Microsoft.EntityFrameworkCore;
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
        => CreateServiceAsync(CreateServerDbFactory());

    private Task<(VersionHistoryService versions, FileStorageService storage, UploadService upload)> CreateServiceAsync(
        IDbContextFactory<CloudPanDbContext> dbFactory)
    {
        var storage = new FileStorageService(TempDir);
        var index = new FileIndexService(dbFactory);
        var version = new VersionService(dbFactory);
        var syncLog = new SyncLogService(dbFactory, NullLogger<SyncLogService>.Instance);
        var helper = new VersionCommitHelper(storage, NullLogger<VersionCommitHelper>.Instance);
        var versions = new VersionHistoryService(dbFactory, storage, index, version, syncLog, helper);
        var fileOps = new FileOperationService(storage, index, version,
            new TrashService(storage, index, version, NullLogger<TrashService>.Instance),
            syncLog, new ConflictBackupHelper(storage, index, version, syncLog),
            versions,
            NullLogger<FileOperationService>.Instance);
        var upload = new UploadService(storage, fileOps, version, dbFactory, NullLogger<UploadService>.Instance, helper);
        return Task.FromResult((versions, storage, upload));
    }

    private static Stream ToStream(string text) => new MemoryStream(Encoding.UTF8.GetBytes(text));

    [Fact]
    public async Task GetVersions_上传两次_包含旧版本记录()
    {
        var (versions, _, upload) = await CreateServiceAsync();
        await using var s1 = ToStream("version 1");
        await upload.UploadAsync("/file.txt", s1, 9, 0, DateTime.UtcNow.ToString("O"), "server");
        await using var s2 = ToStream("version 2 longer");
        await upload.UploadAsync("/file.txt", s2, 16, 0, DateTime.UtcNow.ToString("O"), "server");

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
        await upload.UploadAsync("/file.txt", s1, 8, 0, DateTime.UtcNow.ToString("O"), "server");
        await using var s2 = ToStream("newer content");
        await upload.UploadAsync("/file.txt", s2, 13, 0, DateTime.UtcNow.ToString("O"), "server");

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
        await upload.UploadAsync("/file.txt", s1, 4, 0, DateTime.UtcNow.ToString("O"), "server");

        var result = await versions.RestoreAsync("/file.txt", 999, "server");

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task Restore_Move失败_回滚索引保持与磁盘一致()
    {
        var dbFactory = CreateServerDbFactory();
        var (versions, storage, upload) = await CreateServiceAsync(dbFactory);
        await using var s1 = ToStream("original");
        await upload.UploadAsync("/file.txt", s1, 8, 0, DateTime.UtcNow.ToString("O"), "server");
        await using var s2 = ToStream("newer content");
        await upload.UploadAsync("/file.txt", s2, 13, 0, DateTime.UtcNow.ToString("O"), "server");

        var list = await versions.GetVersionsAsync("/file.txt", 10);
        int v1 = list.Single().Version;

        string targetPath = Path.Combine(TempDir, "file.txt");
        Assert.Equal("newer content", await File.ReadAllTextAsync(targetPath));

        // 以 FileShare.Read 锁定目标文件：允许存档阶段读取，但阻止 Move 覆盖（覆盖需替换/删除目标）
        // → 模拟覆盖阶段的 IO 失败，触发回滚索引失败路径
        Exception? moveError;
        using (var lockStream = new FileStream(targetPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            moveError = await Record.ExceptionAsync(() => versions.RestoreAsync("/file.txt", v1, "server"));
        }
        Assert.NotNull(moveError); // Move 覆盖失败应抛出，不得静默吞掉

        // 1. 磁盘仍为旧内容（Move 未覆盖目标）
        Assert.Equal("newer content", await File.ReadAllTextAsync(targetPath));

        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            // 2. 证明 Restore 确实走到了版本分配/Move 覆盖阶段而非更早步骤失败：
            //    global_version 已消费 3 个（上传 v1、上传 v2、本次 Restore），而非停在 2
            var config = await db.AppConfigs.FindAsync("global_version");
            Assert.Equal("3", config?.Value);

            // 3. 索引与磁盘一致：FileEntry.CurrentHash == 磁盘哈希（客户端下载校验不再失败进入重试循环）
            var entry = await db.FileEntries.FindAsync("/file.txt");
            Assert.NotNull(entry);
            Assert.Equal(await FileHasher.ComputeSha256Async(targetPath), entry!.CurrentHash);
            Assert.Equal(13, entry.CurrentSize);

            // 4. 无本次回滚残留的孤儿版本记录（本次存档 + 回滚记录均已移除；仅剩上传 v2 时产生的存档记录）
            var records = await db.VersionRecords.Where(v => v.FilePath == "/file.txt").ToListAsync();
            Assert.All(records, r => Assert.Null(r.RestoredFromVersion));
        }

        // 5. 临时文件已清理
        Assert.False(File.Exists(targetPath + ".tmp"));

        // 6. 锁释放后重试成功，内容回滚到 v1（客户端重试/下轮扫描按哈希差异收敛自愈）
        var retry = await versions.RestoreAsync("/file.txt", v1, "server");
        Assert.True(retry.Success);
        Assert.Equal("original", await File.ReadAllTextAsync(targetPath));
    }
}
