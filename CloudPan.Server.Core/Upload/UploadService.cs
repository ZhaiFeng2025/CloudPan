using CloudPan.Contract;
using CloudPan.Infrastructure.Models;
using CloudPan.Infrastructure.Persistence;
using CloudPan.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CloudPan.Server.Core;

/// <summary>上传编排中目标文件写入失败（存储不可用），Message 为可直接展示的错误信息。</summary>
public sealed class UploadStorageException : Exception
{
    public UploadStorageException(string message) : base(message) { }
}

/// <inheritdoc />
public class UploadService : IUploadService
{
    private readonly IFileStorageService _storage;
    private readonly IVersionService _version;
    private readonly IDbContextFactory<CloudPanDbContext> _dbFactory;
    private readonly ILogger<UploadService> _logger;
    private readonly VersionCommitHelper _versionCommit;

    public UploadService(
        IFileStorageService storage,
        IVersionService version,
        IDbContextFactory<CloudPanDbContext> dbFactory,
        ILogger<UploadService> logger,
        VersionCommitHelper versionCommit)
    {
        _storage = storage;
        _version = version;
        _dbFactory = dbFactory;
        _logger = logger;
        _versionCommit = versionCommit;
    }

    /// <inheritdoc />
    public async Task<UploadResult> UploadAsync(
        string path, Stream content, long contentLength,
        string? lastModified, string deviceId, CancellationToken ct = default)
    {
        // 1. 先分配版本号，避免孤儿文件
        int newVersion = await _version.NextVersionAsync();

        await using var db = await _dbFactory.CreateDbContextAsync();

        // 2. 先存档旧版本（FS）——必须在原子覆盖前读取旧内容，否则存档读到的是新内容（F-01 缺陷根因）
        var existing = await db.FileEntries.FindAsync(new object?[] { path }, ct);
        string? archiveStoragePath = await _versionCommit.ArchiveOldVersionAsync(path, existing, ct);

        bool targetWritten = false;
        try
        {
            // 3. 再原子覆盖目标文件（存档已完成，旧内容已安全保存，此时覆盖无损版本历史）
            string? writeError = await _storage.AtomicWriteAsync(path, content, expectedHash: null, ct);
            if (writeError != null)
            {
                throw new UploadStorageException(writeError);
            }
            targetWritten = true;

            // 4. 计算新哈希
            string hash = await _storage.ComputeHashAsync(_storage.GetAbsolutePath(path), ct);

            // 5. 『提交新版本』：存档记录 + 裁剪 + upsert FileEntry + 审计日志，单事务单点提交（VersionCommitHelper）
            await _versionCommit.CommitNewVersionInTransactionAsync(
                db, path, existing, archiveStoragePath,
                new VersionCommitState(path, hash, contentLength, newVersion,
                    lastModified ?? DateTime.UtcNow.ToString("O")),
                deviceId, prune: true,
                extraDbWork: () => db.SyncLogs.Add(new SyncLog
                {
                    FilePath = path,
                    Operation = (int)SyncOperation.Upload,
                    DeviceId = deviceId,
                    Result = (int)LogResult.Success,
                    CreatedAt = DateTime.UtcNow.ToString("O")
                }), ct);

            return new UploadResult(path, newVersion, hash, contentLength);
        }
        catch
        {
            // 事务回滚后清理文件系统副作用，避免孤儿文件（CLAUDE.md 7.3）
            if (targetWritten)
            {
                try { _storage.Delete(path); } catch (Exception cleanupEx) { _logger.LogWarning(cleanupEx, "上传回滚后清理目标文件失败: {Path}", path); }
            }
            try { _versionCommit.DeleteOrphanArchive(archiveStoragePath); } catch (Exception cleanupEx) { _logger.LogWarning(cleanupEx, "上传回滚后清理版本存档失败: {Path}", archiveStoragePath); }
            throw;
        }
    }
}
