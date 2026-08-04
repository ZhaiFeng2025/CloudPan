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
    private readonly IFileOperationService _fileOps;
    private readonly IVersionService _version;
    private readonly IDbContextFactory<CloudPanDbContext> _dbFactory;
    private readonly ILogger<UploadService> _logger;
    private readonly VersionCommitHelper _versionCommit;

    public UploadService(
        IFileStorageService storage,
        IFileOperationService fileOps,
        IVersionService version,
        IDbContextFactory<CloudPanDbContext> dbFactory,
        ILogger<UploadService> logger,
        VersionCommitHelper versionCommit)
    {
        _storage = storage;
        _fileOps = fileOps;
        _version = version;
        _dbFactory = dbFactory;
        _logger = logger;
        _versionCommit = versionCommit;
    }

    /// <inheritdoc />
    public async Task<UploadOutcome> UploadAsync(
        string path, Stream content, long contentLength, int baseVersion,
        string? lastModified, string deviceId, CancellationToken ct = default)
    {
        // 路径安全统一防线（F-132）：路径归一 + 校验下沉 Core，不再依赖 Controller 兜底。
        // 任何入口调用（未来后台写入/测试）都先过 ValidatePath，越界在落盘前被拒（对齐分块/文件操作服务）。
        if (!path.StartsWith('/'))
        {
            path = "/" + path;
        }
        string? pathErr = _storage.ValidatePath(path);
        if (pathErr != null)
        {
            return new UploadErrorOutcome(new DomainError(HttpErrorCode.BAD_REQUEST, pathErr, "路径格式不正确"));
        }

        await using var db = await _dbFactory.CreateDbContextAsync();
        var existing = await db.FileEntries.FindAsync(new object?[] { path }, ct);

        // 0. 冲突检测：baseVersion > 0 且服务端当前版本 > baseVersion → 保存冲突副本并返回冲突，
        //    不推进主文件版本（语义与分块上传 ChunkedUploadService.FinalizeAsync 一致，F-56/T-056 下沉载体）。
        if (baseVersion > 0 && existing != null && existing.Version > baseVersion)
        {
            var conflict = await _fileOps.HandleUploadConflictAsync(
                path, content, contentLength, lastModified, baseVersion, existing.Version, deviceId);
            return new UploadConflictOutcome(path, conflict.CurrentVersion, conflict.BaseVersion, conflict.ConflictPath);
        }

        // 1. 先分配版本号，避免孤儿文件
        int newVersion = await _version.NextVersionAsync();

        // 2. 先存档旧版本（FS）——必须在原子覆盖前读取旧内容，否则存档读到的是新内容（F-01 缺陷根因）
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
            string hash = await FileHasher.ComputeSha256Async(_storage.GetAbsolutePath(path), ct);

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

            return new UploadSuccessOutcome(path, newVersion, hash, contentLength);
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
