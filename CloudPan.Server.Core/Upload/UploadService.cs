using CloudPan.Server.Data;
using CloudPan.Server.Models;
using CloudPan.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CloudPan.Server.Services;

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

    public UploadService(
        IFileStorageService storage,
        IVersionService version,
        IDbContextFactory<CloudPanDbContext> dbFactory,
        ILogger<UploadService> logger)
    {
        _storage = storage;
        _version = version;
        _dbFactory = dbFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<UploadResult> UploadAsync(
        string path, Stream content, long contentLength,
        string? lastModified, string deviceId, CancellationToken ct = default)
    {
        // 1. 先分配版本号，避免孤儿文件
        int newVersion = await _version.NextVersionAsync();

        string? archiveStoragePath = null;
        bool targetWritten = false;

        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        try
        {
            // 2. 先存档旧版本（FS）——必须在原子覆盖前读取，否则 StoreVersionAsync 读到的是新内容（F-01 缺陷根因）
            var existing = await db.FileEntries.FindAsync(new object?[] { path }, ct);
            if (existing != null && existing.CurrentHash != null)
            {
                archiveStoragePath = await _storage.StoreVersionAsync(path, existing.Version, ct);

                // 3. 仅在 FS 存档成功后写入 VersionRecord（存档内容 = 上传前旧内容）
                db.VersionRecords.Add(new VersionRecord
                {
                    FilePath = path,
                    Version = existing.Version,
                    Hash = existing.CurrentHash!,
                    Size = existing.CurrentSize,
                    StoragePath = archiveStoragePath,
                    Timestamp = DateTime.UtcNow.ToString("O"),
                    DeviceId = deviceId
                });

                // 保留最近 N 个版本（N 单源：shared-spec.json → SpecConfig.MaxVersionsDefault）
                var oldVersions = await db.VersionRecords
                    .Where(v => v.FilePath == path)
                    .OrderByDescending(v => v.Version)
                    .Skip(SpecConfig.MaxVersionsDefault)
                    .ToListAsync(ct);
                db.VersionRecords.RemoveRange(oldVersions);
            }

            // 4. 再原子覆盖目标文件（存档已完成，旧内容已安全保存，此时覆盖无损版本历史）
            string? writeError = await _storage.AtomicWriteAsync(path, content, expectedHash: null, ct);
            if (writeError != null)
            {
                throw new UploadStorageException(writeError);
            }
            targetWritten = true;

            // 5. 计算新哈希
            string hash = await _storage.ComputeHashAsync(_storage.GetAbsolutePath(path), ct);

            // 6. 后更新索引
            FileEntry? entry = await db.FileEntries.FindAsync(new object?[] { path }, ct);
            if (entry != null)
            {
                entry.CurrentHash = hash;
                entry.CurrentSize = contentLength;
                entry.Version = newVersion;
                entry.LastModified = lastModified ?? DateTime.UtcNow.ToString("O");
                entry.State = (int)FileState.Synced;
            }
            else
            {
                entry = new FileEntry
                {
                    Path = path,
                    Type = (int)FileType.File,
                    CurrentHash = hash,
                    CurrentSize = contentLength,
                    Version = newVersion,
                    LastModified = lastModified ?? DateTime.UtcNow.ToString("O"),
                    State = (int)FileState.Synced,
                    CreatedAt = DateTime.UtcNow.ToString("O")
                };
                db.FileEntries.Add(entry);
            }

            // 7. 审计日志（同一事务）
            db.SyncLogs.Add(new SyncLog
            {
                FilePath = entry.Path,
                Operation = (int)SyncOperation.Upload,
                DeviceId = deviceId,
                Result = (int)LogResult.Success,
                CreatedAt = DateTime.UtcNow.ToString("O")
            });

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            return new UploadResult(path, newVersion, hash, contentLength);
        }
        catch
        {
            await tx.RollbackAsync();
            // 事务回滚后清理文件系统副作用，避免孤儿文件（CLAUDE.md 7.3）
            if (targetWritten)
            {
                try { _storage.Delete(path); } catch (Exception cleanupEx) { _logger.LogWarning(cleanupEx, "上传回滚后清理目标文件失败: {Path}", path); }
            }
            if (archiveStoragePath != null)
            {
                try { _storage.Delete(archiveStoragePath); } catch (Exception cleanupEx) { _logger.LogWarning(cleanupEx, "上传回滚后清理版本存档失败: {Path}", archiveStoragePath); }
            }
            throw;
        }
    }
}
