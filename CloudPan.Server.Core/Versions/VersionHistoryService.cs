using CloudPan.Server.Data;
using CloudPan.Server.Models;
using CloudPan.Shared;
using Microsoft.EntityFrameworkCore;
using IOFile = System.IO.File;
using IOFileInfo = System.IO.FileInfo;

namespace CloudPan.Server.Services;

/// <inheritdoc />
public class VersionHistoryService : IVersionHistoryService
{
    private readonly IDbContextFactory<CloudPanDbContext> _dbFactory;
    private readonly IFileStorageService _storage;
    private readonly IFileIndexService _index;
    private readonly IVersionService _version;
    private readonly ISyncLogService _syncLog;

    public VersionHistoryService(
        IDbContextFactory<CloudPanDbContext> dbFactory,
        IFileStorageService storage,
        IFileIndexService index,
        IVersionService version,
        ISyncLogService syncLog)
    {
        _dbFactory = dbFactory;
        _storage = storage;
        _index = index;
        _version = version;
        _syncLog = syncLog;
    }

    /// <inheritdoc />
    public async Task<List<VersionRecordInfo>> GetVersionsAsync(string path, int limit)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var versions = await db.VersionRecords
            .Where(v => v.FilePath == path)
            .OrderByDescending(v => v.Version)
            .Take(Math.Min(limit, 50))
            .Select(v => new VersionRecordInfo(v.Version, v.Hash, v.Size, v.Timestamp, v.DeviceId, v.RestoredFromVersion))
            .ToListAsync();
        return versions;
    }

    /// <inheritdoc />
    public async Task<VersionRestoreResult> RestoreAsync(string filePath, int version, string deviceId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        // 查找目标版本
        var targetVersion = await db.VersionRecords
            .FirstOrDefaultAsync(v => v.FilePath == filePath && v.Version == version);
        if (targetVersion == null)
        {
            return new VersionRestoreResult(false, null, null, null, null, null,
                new DomainError(HttpErrorCode.NOT_FOUND, $"版本不存在: v{version}", "指定的历史版本不存在"));
        }

        // 检查历史文件（版本存档固定位于同步根 .cloudpan/.versions/ 下，与 StoragePath 相对路径拼接）
        string versionFilePath = Path.Combine(
            _storage.GetAbsolutePath("/"),
            ".cloudpan", ".versions", targetVersion.StoragePath);
        if (!IOFile.Exists(versionFilePath))
        {
            return new VersionRestoreResult(false, null, null, null, null, null,
                new DomainError(HttpErrorCode.NOT_FOUND, "历史版本文件已丢失", "历史版本文件已丢失，无法恢复"));
        }

        var currentEntry = await _index.GetByPathAsync(filePath);
        if (currentEntry == null)
        {
            return new VersionRestoreResult(false, null, null, null, null, null,
                new DomainError(HttpErrorCode.NOT_FOUND, $"文件不存在: {filePath}", "文件不存在，无法恢复"));
        }

        // 1. 存档当前版本（FS）
        string storagePath = await _storage.StoreVersionAsync(filePath, currentEntry.Version);

        // 2. 复制历史版本到临时文件（不覆盖目标文件——DB 失败时目标文件保持当前版本，可恢复）
        string targetPath = _storage.GetAbsolutePath(filePath);
        string tmpPath = targetPath + ".tmp";
        using (var srcStream = IOFile.OpenRead(versionFilePath))
        using (var dstStream = IOFile.Create(tmpPath))
        {
            await srcStream.CopyToAsync(dstStream);
            await dstStream.FlushAsync();
        }

        // 3. 计算新版本号与哈希（对 tmp 计算，不依赖已覆盖的目标文件）
        int newVersion = await _version.NextVersionAsync();
        string hash = await _storage.ComputeHashAsync(tmpPath);
        long size = new IOFileInfo(tmpPath).Length;

        // 4. 单事务 DB 写入（存档记录 + FileEntry 更新 + 回滚记录），同一 DbContext。
        //    DB 失败则清理 tmp、不覆盖目标文件（文件保持当前版本）；成功后再原子覆盖目标。
        await using var tx = await db.Database.BeginTransactionAsync();
        try
        {
            db.VersionRecords.Add(new VersionRecord
            {
                FilePath = filePath,
                Version = currentEntry.Version,
                Hash = currentEntry.CurrentHash ?? "",
                Size = currentEntry.CurrentSize,
                StoragePath = storagePath,
                Timestamp = DateTime.UtcNow.ToString("O"),
                DeviceId = deviceId
            });

            // 更新 FileEntry 为新版本（同一 DbContext，避免跨上下文不一致）
            var entry = await db.FileEntries.FindAsync(filePath);
            if (entry != null)
            {
                entry.CurrentHash = hash;
                entry.CurrentSize = size;
                entry.Version = newVersion;
                entry.LastModified = DateTime.UtcNow.ToString("O");
                entry.State = (int)FileState.Synced;
            }

            db.VersionRecords.Add(new VersionRecord
            {
                FilePath = filePath,
                Version = newVersion,
                Hash = hash,
                Size = size,
                StoragePath = targetVersion.StoragePath, // 回滚后内容 = 历史版本文件
                Timestamp = DateTime.UtcNow.ToString("O"),
                DeviceId = deviceId,
                RestoredFromVersion = version
            });

            await db.SaveChangesAsync();
            await tx.CommitAsync();
        }
        catch
        {
            await tx.RollbackAsync();
            if (IOFile.Exists(tmpPath))
            {
                try { IOFile.Delete(tmpPath); } catch { /* 尽力清理 */ }
            }
            throw;
        }

        // 5. 原子覆盖目标文件（DB 已提交；Move 失败时文件保持旧内容，下次同步按哈希重传自愈）
        try
        {
            IOFile.Move(tmpPath, targetPath, overwrite: true);
        }
        catch
        {
            if (IOFile.Exists(tmpPath))
            {
                try { IOFile.Delete(tmpPath); } catch { /* 尽力清理 */ }
            }
            throw;
        }

        // 写入审计日志（回滚成功）
        await _syncLog.LogAsync(filePath, SyncOperation.Restore, deviceId, LogResult.Success,
            $"回滚到 v{version}");

        return new VersionRestoreResult(true, filePath, newVersion, hash, size, version);
    }
}
