using CloudPan.Contract;
using CloudPan.Infrastructure.Storage;
using CloudPan.Server.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CloudPan.Tests.Server.Services;

/// <summary>
/// UploadService 单元测试——普通上传冲突路径与正常覆盖（脱离 ASP.NET，直接注入领域服务）。
/// 冲突判定与冲突副本保存在 Core 内执行（F-56/T-056），与分块上传路径（ChunkedUploadService）行为一致。
/// </summary>
public class UploadServiceTests : Infrastructure.TestBase
{
    private string SyncRoot => Path.Combine(TempDir, "sync");

    private Task<(UploadService svc, FileIndexService index, VersionService version)> CreateServiceAsync()
    {
        var dbFactory = CreateServerDbFactory();
        var storage = new FileStorageService(SyncRoot);
        var index = new FileIndexService(dbFactory);
        var version = new VersionService(dbFactory);
        var syncLog = new SyncLogService(dbFactory, NullLogger<SyncLogService>.Instance);
        var fileOps = new FileOperationService(storage, index, version,
            new TrashService(storage, index, version, NullLogger<TrashService>.Instance),
            syncLog, new ConflictBackupHelper(storage, index, version, syncLog),
            NullLogger<FileOperationService>.Instance);
        var svc = new UploadService(storage, fileOps, version, dbFactory,
            NullLogger<UploadService>.Instance,
            new VersionCommitHelper(storage, NullLogger<VersionCommitHelper>.Instance));
        return Task.FromResult((svc, index, version));
    }

    /// <summary>
    /// 普通上传冲突：服务端当前版本（5）> 客户端 baseVersion（3）→ 保存冲突副本并返回 UploadConflictOutcome，
    /// 不推进主文件版本、不覆盖主文件（语义与分块上传 FinalizeAsync 冲突分支一致）。
    /// </summary>
    [Fact]
    public async Task Upload_服务端版本大于baseVersion_保存冲突副本不覆盖原文件()
    {
        var (svc, index, _) = await CreateServiceAsync();
        string path = "/conflict.bin";
        string targetPath = Path.Combine(SyncRoot, "conflict.bin");

        // 服务端已有 v5
        byte[] oldContent = CreateContent(300);
        string oldLastModified = DateTime.UtcNow.AddHours(-1).ToString("O");
        File.WriteAllBytes(targetPath, oldContent);
        await index.UpsertFileAsync(path, FileType.File, "oldhash", oldContent.Length, oldLastModified, 5, FileState.Synced);

        // 客户端基于 v3 上传新内容 → 冲突（SyncLog 无 Device 外键，deviceId 用 dev-1 即可）
        byte[] newContent = CreateContent(400);
        using var stream = new MemoryStream(newContent);
        var outcome = await svc.UploadAsync(path, stream, newContent.Length, baseVersion: 3, null, "dev-1");

        var conflict = Assert.IsType<UploadConflictOutcome>(outcome);
        Assert.Equal(5, conflict.CurrentVersion);
        Assert.Equal(3, conflict.BaseVersion);
        Assert.Contains("冲突", conflict.ConflictPath);
        // 冲突副本已写入且索引为 Conflict
        string conflictAbs = Path.Combine(SyncRoot, conflict.ConflictPath.TrimStart('/'));
        Assert.True(File.Exists(conflictAbs));
        var conflictEntry = await index.GetByPathAsync(conflict.ConflictPath);
        Assert.NotNull(conflictEntry);
        Assert.Equal((int)FileState.Conflict, conflictEntry!.State);
        // 主文件未被覆盖：内容与版本保持 v5
        Assert.Equal(oldContent, await File.ReadAllBytesAsync(targetPath));
        Assert.Equal(5, (await index.GetByPathAsync(path))!.Version);
    }

    /// <summary>
    /// 普通上传正常覆盖：客户端 baseVersion 与服务端当前版本匹配 → 覆盖成功返回 UploadSuccessOutcome，
    /// 新内容落盘、版本号提升、索引指向新哈希。
    /// </summary>
    [Fact]
    public async Task Upload_baseVersion匹配_正常覆盖返回新版本()
    {
        var (svc, index, version) = await CreateServiceAsync();
        string path = "/success.bin";
        string targetPath = Path.Combine(SyncRoot, "success.bin");

        // 服务端已有旧版本（经 VersionService 分配，保证版本号单调，与分块上传测试一致；
        // VersionRecord 有 Device 外键，成功路径 deviceId 用种子设备 server）
        byte[] oldContent = CreateContent(300);
        int oldVersion = await version.NextVersionAsync();
        File.WriteAllBytes(targetPath, oldContent);
        await index.UpsertFileAsync(path, FileType.File, "oldhash", oldContent.Length,
            DateTime.UtcNow.AddHours(-1).ToString("O"), oldVersion, FileState.Synced);

        // baseVersion 与服务端当前版本匹配 → 正常覆盖
        byte[] newContent = CreateContent(400);
        using var stream = new MemoryStream(newContent);
        var outcome = await svc.UploadAsync(path, stream, newContent.Length, baseVersion: oldVersion, null, "server");

        var success = Assert.IsType<UploadSuccessOutcome>(outcome);
        Assert.True(success.Version > oldVersion);
        Assert.Equal(newContent, await File.ReadAllBytesAsync(targetPath));
        var entry = await index.GetByPathAsync(path);
        Assert.NotNull(entry);
        Assert.Equal(success.Hash, entry!.CurrentHash);
        Assert.Equal(success.Version, entry.Version);
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
