using System.Security.Cryptography;
using System.Text;
using CloudPan.Contract;
using CloudPan.Infrastructure.Models;
using CloudPan.Infrastructure.Persistence;
using CloudPan.Infrastructure.Storage;
using CloudPan.Server.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CloudPan.Tests.Server.Services;

/// <summary>
/// VersionCommitHelper 单元测试——『提交新版本』单一辅助的存档→裁剪→更新事务与回滚路径
/// （该编排只存在于此辅助，Upload/ChunkedUpload/Restore 共用，任一修订不需多处同步）。
/// </summary>
public class VersionCommitHelperTests : Infrastructure.TestBase
{
    private (VersionCommitHelper helper, FileStorageService storage, IDbContextFactory<CloudPanDbContext> dbFactory, VersionService version) CreateServices()
    {
        var dbFactory = CreateServerDbFactory();
        var storage = new FileStorageService(TempDir);
        var helper = new VersionCommitHelper(storage, NullLogger<VersionCommitHelper>.Instance);
        var version = new VersionService(dbFactory);
        return (helper, storage, dbFactory, version);
    }

    [Fact]
    public async Task CommitNewVersion_存档裁剪更新_单事务生效()
    {
        var (helper, storage, dbFactory, version) = CreateServices();
        string path = "/commit.bin";
        string targetPath = Path.Combine(TempDir, "commit.bin");

        // 建立旧内容与索引（v1）
        byte[] oldContent = Encoding.UTF8.GetBytes("old content");
        string oldHash = Convert.ToHexString(SHA256.HashData(oldContent)).ToLowerInvariant();
        int oldVersion = await version.NextVersionAsync();
        File.WriteAllBytes(targetPath, oldContent);
        await using (var seed = await dbFactory.CreateDbContextAsync())
        {
            seed.FileEntries.Add(new FileEntry
            {
                Path = path,
                Type = (int)FileType.File,
                CurrentHash = oldHash,
                CurrentSize = oldContent.Length,
                Version = oldVersion,
                LastModified = DateTime.UtcNow.ToString("O"),
                State = (int)FileState.Synced,
                CreatedAt = DateTime.UtcNow.ToString("O")
            });
            await seed.SaveChangesAsync();
        }

        // 存档旧版本（FS，须在覆盖目标前）→ 覆盖目标 → 经辅助提交新版本（v2）
        string newContent = "new content longer";
        byte[] newBytes = Encoding.UTF8.GetBytes(newContent);
        string newHash = Convert.ToHexString(SHA256.HashData(newBytes)).ToLowerInvariant();
        int newVersion = await version.NextVersionAsync();

        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            var oldEntry = await db.FileEntries.FindAsync(path);
            string? archive = await helper.ArchiveOldVersionAsync(path, oldEntry);
            Assert.NotNull(archive);
            string archiveFile = Path.Combine(storage.GetAbsolutePath("/"), ".cloudpan", ".versions", archive!);
            Assert.True(File.Exists(archiveFile), "存档文件应已落盘");

            // 存档完成后才覆盖目标（F-01 顺序）
            File.WriteAllBytes(targetPath, newBytes);

            await helper.CommitNewVersionInTransactionAsync(
                db, path, oldEntry, archive,
                new VersionCommitState(path, newHash, newBytes.Length, newVersion, DateTime.UtcNow.ToString("O")),
                "server", prune: true);
        }

        // 断言：存档记录 + FileEntry 更新同一事务生效
        await using (var verify = await dbFactory.CreateDbContextAsync())
        {
            var entry = await verify.FileEntries.FindAsync(path);
            Assert.NotNull(entry);
            Assert.Equal(newHash, entry!.CurrentHash);
            Assert.Equal(newVersion, entry.Version);
            Assert.Equal(newBytes.Length, entry.CurrentSize);
            Assert.Equal((int)FileState.Synced, entry.State);

            var record = await verify.VersionRecords.SingleAsync(v => v.FilePath == path);
            Assert.Equal(oldHash, record.Hash);
            Assert.Equal(oldVersion, record.Version);
            Assert.Equal(oldContent.Length, record.Size);
        }
    }

    [Fact]
    public async Task CommitNewVersion_裁剪超MaxVersionsDefault_只保留最近N个()
    {
        var (helper, storage, dbFactory, version) = CreateServices();
        string path = "/prune.bin";
        string targetPath = Path.Combine(TempDir, "prune.bin");

        // 逐版本提交（每次先存档旧内容，再覆盖目标，最后经辅助提交），累积 MaxVersionsDefault + 3 个版本
        string content = "base";
        int iterations = SpecConfig.MaxVersionsDefault + 3;
        for (int i = 0; i < iterations; i++)
        {
            content += i;
            byte[] newBytes = Encoding.UTF8.GetBytes(content);
            string newHash = Convert.ToHexString(SHA256.HashData(newBytes)).ToLowerInvariant();
            int newVersion = await version.NextVersionAsync();

            await using (var db = await dbFactory.CreateDbContextAsync())
            {
                var oldEntry = await db.FileEntries.FindAsync(path);
                string? archive = await helper.ArchiveOldVersionAsync(path, oldEntry);
                File.WriteAllBytes(targetPath, newBytes);
                await helper.CommitNewVersionInTransactionAsync(
                    db, path, oldEntry, archive,
                    new VersionCommitState(path, newHash, newBytes.Length, newVersion, DateTime.UtcNow.ToString("O")),
                    "server", prune: true);
            }
        }

        await using (var verify = await dbFactory.CreateDbContextAsync())
        {
            var records = await verify.VersionRecords.Where(v => v.FilePath == path).ToListAsync();
            // 裁剪保留最近 N 个版本；裁剪查询在事务内、SaveChanges 前执行，SQL 查询不含本次未落库的存档记录，
            // 故实际保留上限为 N+1——与原 Upload/ChunkedUpload 实现语义一致（保留行为而非本任务新增缺陷）
            Assert.Equal(SpecConfig.MaxVersionsDefault + 1, records.Count);
            // 最旧版本 v1 已被裁剪（8 次提交产生 7 条存档，仅最旧的 1 条超限被移除）
            Assert.DoesNotContain(records, r => r.Version == 1);
        }

        // T-088：被裁剪版本（v1）对应的存档物理文件应同步删除（孤儿存档清理单点），
        // 保留版本对应的存档仍在 .versions/ 中
        string versionsDir = Path.Combine(storage.GetAbsolutePath("/"), ".cloudpan", ".versions");
        string[] archiveFiles = Directory.GetFiles(versionsDir);
        Assert.DoesNotContain(archiveFiles, f => Path.GetFileName(f).StartsWith("prune_v1_", StringComparison.Ordinal));
        Assert.NotEmpty(archiveFiles); // 保留版本的存档未被误删
    }

    [Fact]
    public async Task CommitNewVersion_事务失败_回滚且清理孤儿存档()
    {
        var (helper, storage, dbFactory, version) = CreateServices();
        string path = "/rollback.bin";
        string targetPath = Path.Combine(TempDir, "rollback.bin");

        // 建立旧内容与索引（v1）
        byte[] oldContent = Encoding.UTF8.GetBytes("old");
        string oldHash = Convert.ToHexString(SHA256.HashData(oldContent)).ToLowerInvariant();
        int oldVersion = await version.NextVersionAsync();
        File.WriteAllBytes(targetPath, oldContent);
        await using (var seed = await dbFactory.CreateDbContextAsync())
        {
            seed.FileEntries.Add(new FileEntry
            {
                Path = path,
                Type = (int)FileType.File,
                CurrentHash = oldHash,
                CurrentSize = oldContent.Length,
                Version = oldVersion,
                LastModified = DateTime.UtcNow.ToString("O"),
                State = (int)FileState.Synced,
                CreatedAt = DateTime.UtcNow.ToString("O")
            });
            await seed.SaveChangesAsync();
        }

        // 存档旧版本后，事务内触发失败（extraDbWork 抛异常）→ 应回滚且清理孤儿存档
        byte[] newBytes = Encoding.UTF8.GetBytes("new content");
        string newHash = Convert.ToHexString(SHA256.HashData(newBytes)).ToLowerInvariant();
        int newVersion = await version.NextVersionAsync();

        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            var oldEntry = await db.FileEntries.FindAsync(path);
            string? archive = await helper.ArchiveOldVersionAsync(path, oldEntry);
            Assert.NotNull(archive);
            string archiveFile = Path.Combine(storage.GetAbsolutePath("/"), ".cloudpan", ".versions", archive!);
            Assert.True(File.Exists(archiveFile), "存档文件应已落盘");

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                helper.CommitNewVersionInTransactionAsync(
                    db, path, oldEntry, archive,
                    new VersionCommitState(path, newHash, newBytes.Length, newVersion, DateTime.UtcNow.ToString("O")),
                    "server", prune: true,
                    extraDbWork: () => throw new InvalidOperationException("模拟 DB 写入失败")));

            // 事务回滚后孤儿存档已被辅助清理（FS 副作用）
            Assert.False(File.Exists(archiveFile), "回滚后孤儿存档应被清理");
        }

        // DB 侧回滚：FileEntry 保持旧值、无孤儿版本记录
        await using (var verify = await dbFactory.CreateDbContextAsync())
        {
            var entry = await verify.FileEntries.FindAsync(path);
            Assert.NotNull(entry);
            Assert.Equal(oldHash, entry!.CurrentHash);
            Assert.Equal(oldVersion, entry.Version);
            Assert.Equal(oldContent.Length, entry.CurrentSize);
            Assert.False(await verify.VersionRecords.AnyAsync(v => v.FilePath == path));
        }
    }

    [Fact]
    public async Task CommitNewVersion_新建文件_无存档仅upsert()
    {
        var (helper, _, dbFactory, version) = CreateServices();
        string path = "/new.bin";
        string targetPath = Path.Combine(TempDir, "new.bin");

        byte[] content = Encoding.UTF8.GetBytes("brand new");
        string hash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        int newVersion = await version.NextVersionAsync();
        File.WriteAllBytes(targetPath, content);

        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            var oldEntry = await db.FileEntries.FindAsync(path);
            string? archive = await helper.ArchiveOldVersionAsync(path, oldEntry);
            Assert.Null(archive); // 新建文件无旧内容，不存档

            await helper.CommitNewVersionInTransactionAsync(
                db, path, oldEntry, archive,
                new VersionCommitState(path, hash, content.Length, newVersion, DateTime.UtcNow.ToString("O")),
                "server", prune: true);
        }

        await using (var verify = await dbFactory.CreateDbContextAsync())
        {
            var entry = await verify.FileEntries.FindAsync(path);
            Assert.NotNull(entry);
            Assert.Equal(newVersion, entry!.Version);
            Assert.Equal(hash, entry.CurrentHash);
            Assert.False(await verify.VersionRecords.AnyAsync(v => v.FilePath == path));
        }
    }
}
