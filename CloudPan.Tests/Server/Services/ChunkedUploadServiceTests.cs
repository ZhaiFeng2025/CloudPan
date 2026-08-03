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
/// ChunkedUploadService 单元测试——分块接收、幂等跳过、合并完成（脱离 ASP.NET，直接注入领域服务）。
/// </summary>
public class ChunkedUploadServiceTests : Infrastructure.TestBase
{
    private ChunkedUploadService CreateServiceAsync()
    {
        var dbFactory = CreateServerDbFactory();
        var storage = new FileStorageService(TempDir);
        var index = new FileIndexService(dbFactory);
        var version = new VersionService(dbFactory);
        var syncLog = new SyncLogService(dbFactory, NullLogger<SyncLogService>.Instance);
        return new ChunkedUploadService(dbFactory, storage, index, version, syncLog,
            new VersionCommitHelper(storage, NullLogger<VersionCommitHelper>.Instance));
    }

    [Fact]
    public async Task ReceiveChunk_两块_完成后合并文件一致()
    {
        var svc = CreateServiceAsync();
        // 真实客户端按 SpecConfig.ChunkSize 定长切块（仅末块可短），测试对齐该语义（块 1 落在 offset=ChunkSize）
        byte[] bytes = CreateContent(SpecConfig.ChunkSize + 100);
        string hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        using var s1 = new MemoryStream(bytes, 0, SpecConfig.ChunkSize);
        var out1 = await svc.ReceiveChunkAsync("/upload.bin", 0, 2, hash, 0, null, "dev-1", s1);
        Assert.IsType<ChunkProgressOutcome>(out1);

        using var s2 = new MemoryStream(bytes, SpecConfig.ChunkSize, 100);
        var out2 = await svc.ReceiveChunkAsync("/upload.bin", 1, 2, hash, 0, null, "dev-1", s2);
        var completed = Assert.IsType<ChunkCompletedOutcome>(out2);
        Assert.Equal(hash, completed.Hash);
        Assert.Equal(bytes.Length, completed.Size);

        byte[] merged = await File.ReadAllBytesAsync(Path.Combine(TempDir, "upload.bin"));
        Assert.Equal(bytes, merged);
    }

    [Fact]
    public async Task ReceiveChunk_重复非首块_幂等跳过()
    {
        var svc = CreateServiceAsync();
        // 3 块：ChunkSize + 100 + 100（末块可短）
        byte[] bytes = CreateContent(SpecConfig.ChunkSize + 200);
        string hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        using var s0 = new MemoryStream(bytes, 0, SpecConfig.ChunkSize);
        await svc.ReceiveChunkAsync("/idem.bin", 0, 3, hash, 0, null, "dev-1", s0);
        using var s1 = new MemoryStream(bytes, SpecConfig.ChunkSize, 100);
        await svc.ReceiveChunkAsync("/idem.bin", 1, 3, hash, 0, null, "dev-1", s1);

        // 重发 chunk 1（非首块）→ 已接收，跳过（receivedCount 仍为 2）
        using var dup = new MemoryStream(bytes, SpecConfig.ChunkSize, 100);
        var outcome = await svc.ReceiveChunkAsync("/idem.bin", 1, 3, hash, 0, null, "dev-1", dup);

        var progress = Assert.IsType<ChunkProgressOutcome>(outcome);
        Assert.Equal(2, progress.ReceivedCount);
    }

    [Fact]
    public async Task ReceiveChunk_崩溃后重发同块_合并SHA256一致()
    {
        var svc = CreateServiceAsync();
        string path = "/recover.bin";
        // 2 块：ChunkSize（块 0）+ 100（块 1，末块可短）
        byte[] bytes = CreateContent(SpecConfig.ChunkSize + 100);
        string hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        // 第 0 块正常接收，位图 [0]
        using var s0 = new MemoryStream(bytes, 0, SpecConfig.ChunkSize);
        var out0 = await svc.ReceiveChunkAsync(path, 0, 2, hash, 0, null, "dev-1", s0);
        Assert.IsType<ChunkProgressOutcome>(out0);

        // 模拟崩溃窗口：第 1 块字节已落盘（按块索引 seek 写入 offset=ChunkSize），但 ReceivedChunks 位图未更新（仍为 [0]）
        // 直接建轻量只读上下文（不再调用 CreateServerDbFactory，避免重复播种种子数据导致主键冲突）
        string tempPath;
        var inspectOptions = new DbContextOptionsBuilder<CloudPanDbContext>()
            .UseSqlite($"Data Source={Path.Combine(TempDir, "test.db")}")
            .Options;
        await using (var db = new CloudPanDbContext(inspectOptions))
        {
            var record = await db.ChunkedUploads.FindAsync(path);
            Assert.NotNull(record);
            tempPath = record.TempPath;
        }
        await using (FileStream fs = new FileStream(tempPath, FileMode.Open, FileAccess.Write))
        {
            fs.Seek(SpecConfig.ChunkSize, SeekOrigin.Begin);
            await fs.WriteAsync(bytes, SpecConfig.ChunkSize, 100);
            await fs.FlushAsync();
        }

        // 客户端查询状态发现块 1 未标记已收 → 重发同块（同内容）
        using var s1 = new MemoryStream(bytes, SpecConfig.ChunkSize, 100);
        var out1 = await svc.ReceiveChunkAsync(path, 1, 2, hash, 0, null, "dev-1", s1);

        // 重发覆盖同位置不产生重复字节：合并后 SHA-256 与完整文件一致
        var completed = Assert.IsType<ChunkCompletedOutcome>(out1);
        Assert.Equal(hash, completed.Hash);
        Assert.Equal(bytes.Length, completed.Size);

        byte[] merged = await File.ReadAllBytesAsync(Path.Combine(TempDir, "recover.bin"));
        Assert.Equal(bytes, merged);
    }

    [Fact]
    public async Task ReceiveChunk_哈希不匹配_返回错误()
    {
        var svc = CreateServiceAsync();
        byte[] bytes = Encoding.UTF8.GetBytes("some bytes");
        string wrongHash = new string('0', 64);

        using var s1 = new MemoryStream(bytes);
        var outcome = await svc.ReceiveChunkAsync("/bad.bin", 0, 1, wrongHash, 0, null, "dev-1", s1);

        Assert.IsType<ChunkErrorOutcome>(outcome);
    }

    [Fact]
    public async Task GetStatus_无会话_返回Found为false()
    {
        var svc = CreateServiceAsync();

        var status = await svc.GetStatusAsync("/never-started.bin");

        Assert.False(status.Found);
    }

    [Fact]
    public async Task GetStatus_有会话_返回进度()
    {
        var svc = CreateServiceAsync();
        byte[] bytes = Encoding.UTF8.GetBytes("status check");
        string hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        using var s1 = new MemoryStream(bytes);
        await svc.ReceiveChunkAsync("/status.bin", 0, 2, hash, 0, null, "dev-1", s1);

        var status = await svc.GetStatusAsync("/status.bin");

        Assert.True(status.Found);
        Assert.Single(status.ReceivedChunks!);
        Assert.Equal(2, status.TotalChunks);
        Assert.False(status.IsComplete);
    }

    [Fact]
    public async Task ReceiveChunk_Move覆盖目标失败_回滚FileEntry到旧hash与version_重试收敛()
    {
        var dbFactory = CreateServerDbFactory();
        var storage = new FileStorageService(TempDir);
        var index = new FileIndexService(dbFactory);
        var version = new VersionService(dbFactory);
        var syncLog = new SyncLogService(dbFactory, NullLogger<SyncLogService>.Instance);
        var svc = new ChunkedUploadService(dbFactory, storage, index, version, syncLog,
            new VersionCommitHelper(storage, NullLogger<VersionCommitHelper>.Instance));

        string path = "/finalize.bin";
        string targetPath = Path.Combine(TempDir, "finalize.bin");

        // 旧文件已存在于磁盘与索引（版本经 VersionService 分配，保证版本号单调）。
        // deviceId 用种子设备 "server"（VersionRecord.DeviceId 有 FK 指向 Device，测试库仅播种 server）
        byte[] oldContent = CreateContent(500);
        string oldHash = Convert.ToHexString(SHA256.HashData(oldContent)).ToLowerInvariant();
        string oldLastModified = DateTime.UtcNow.AddHours(-1).ToString("O");
        int oldVersion = await version.NextVersionAsync();
        File.WriteAllBytes(targetPath, oldContent);
        await index.UpsertFileAsync(path, FileType.File, oldHash, oldContent.Length, oldLastModified, oldVersion, FileState.Synced);

        // 新内容（2 块：ChunkSize + 100，末块可短）
        byte[] newContent = CreateContent(SpecConfig.ChunkSize + 100);
        string newHash = Convert.ToHexString(SHA256.HashData(newContent)).ToLowerInvariant();

        // 锁定目标文件：FileShare.ReadWrite 允许存档阶段读取旧内容，但拒绝重命名/覆盖 → Move 必然失败（文件被锁场景）
        await using var lockStream = new FileStream(targetPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        // 第 0 块（进度），捕获临时文件路径
        using var s0 = new MemoryStream(newContent, 0, SpecConfig.ChunkSize);
        var out0 = await svc.ReceiveChunkAsync(path, 0, 2, newHash, 0, null, "server", s0);
        Assert.IsType<ChunkProgressOutcome>(out0);

        string tempPath;
        await using (var inspect = await dbFactory.CreateDbContextAsync())
        {
            tempPath = (await inspect.ChunkedUploads.FindAsync(path))!.TempPath;
        }

        // 第 1 块（末块）→ FinalizeAsync：DB 事务提交后 Move 覆盖目标失败（目标被锁）→ 触发索引回滚
        using var s1 = new MemoryStream(newContent, SpecConfig.ChunkSize, 100);
        await Assert.ThrowsAnyAsync<Exception>(() => svc.ReceiveChunkAsync(path, 1, 2, newHash, 0, null, "server", s1));

        // 断言回滚：FileEntry 恢复旧 hash/version/size/LastModified，无孤儿版本记录，会话已清理，磁盘仍是旧内容，临时文件已删
        await using (var verify = await dbFactory.CreateDbContextAsync())
        {
            var entry = await verify.FileEntries.FindAsync(path);
            Assert.NotNull(entry);
            Assert.Equal(oldHash, entry!.CurrentHash);
            Assert.Equal(oldVersion, entry.Version);
            Assert.Equal(oldContent.Length, entry.CurrentSize);
            Assert.Equal(oldLastModified, entry.LastModified);
            Assert.Equal((int)FileState.Synced, entry.State);

            Assert.False(await verify.VersionRecords.AnyAsync(v => v.FilePath == path));
            Assert.Null(await verify.ChunkedUploads.FindAsync(path));
        }
        Assert.Equal(oldContent, await File.ReadAllBytesAsync(targetPath));
        Assert.False(File.Exists(tempPath));

        // 释放锁 → 客户端重试（全新会话）→ Move 成功，索引与磁盘收敛到新内容
        await lockStream.DisposeAsync();
        using var r0 = new MemoryStream(newContent, 0, SpecConfig.ChunkSize);
        await svc.ReceiveChunkAsync(path, 0, 2, newHash, 0, null, "server", r0);
        using var r1 = new MemoryStream(newContent, SpecConfig.ChunkSize, 100);
        var retryOutcome = await svc.ReceiveChunkAsync(path, 1, 2, newHash, 0, null, "server", r1);
        var completed = Assert.IsType<ChunkCompletedOutcome>(retryOutcome);
        Assert.Equal(newHash, completed.Hash);
        Assert.Equal(newContent, await File.ReadAllBytesAsync(targetPath));

        await using (var verify = await dbFactory.CreateDbContextAsync())
        {
            var entry = await verify.FileEntries.FindAsync(path);
            Assert.Equal(newHash, entry!.CurrentHash);
            Assert.Equal(completed.Version, entry.Version);
        }
    }

    /// <summary>生成确定性字节内容（模式填充，非随机，便于断言与哈希复现）。</summary>
    private static byte[] CreateContent(int length)
    {
        var content = new byte[length];
        for (int i = 0; i < length; i++)
        {
            content[i] = (byte)(i % 251);
        }
        return content;
    }
}
