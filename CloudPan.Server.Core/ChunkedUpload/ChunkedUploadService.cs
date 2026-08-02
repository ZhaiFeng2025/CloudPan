using System.Text.Json;
using CloudPan.Server.Data;
using CloudPan.Server.Models;
using CloudPan.Shared;
using Microsoft.EntityFrameworkCore;
using IOFile = System.IO.File;

namespace CloudPan.Server.Services;

/// <inheritdoc />
public class ChunkedUploadService : IChunkedUploadService
{
    private const int ChunkedUploadTimeoutMinutes = 1440; // 24h

    private readonly IDbContextFactory<CloudPanDbContext> _dbFactory;
    private readonly IFileStorageService _storage;
    private readonly IFileIndexService _index;
    private readonly IVersionService _version;
    private readonly ISyncLogService _syncLog;

    public ChunkedUploadService(
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
    public async Task<ChunkUploadOutcome> ReceiveChunkAsync(
        string path, int chunkIndex, int totalChunks, string fileHash,
        int baseVersion, string? lastModified, string deviceId, Stream chunkContent)
    {
        // 路径安全统一防线
        string? pathErr = _storage.ValidatePath(path);
        if (pathErr != null)
        {
            return new ChunkErrorOutcome(new DomainError(HttpErrorCode.BAD_REQUEST, pathErr, "路径格式不正确"));
        }

        await using var db = await _dbFactory.CreateDbContextAsync();

        // 查找或创建 ChunkedUpload 记录
        ChunkedUpload? record;

        if (chunkIndex == 0)
        {
            // 清理超时的旧记录 + 临时文件
            string expiryTime = DateTime.UtcNow.AddMinutes(-ChunkedUploadTimeoutMinutes).ToString("O");
            var stale = await db.ChunkedUploads
                .Where(c => c.FilePath == path && string.Compare(c.CreatedAt, expiryTime) < 0)
                .ToListAsync();
            foreach (var s in stale)
            {
                SafeDeleteTemp(s.TempPath);
                db.ChunkedUploads.Remove(s);
            }

            // 检查是否已有同设备同路径的上传记录
            record = await db.ChunkedUploads.FindAsync(path);
            if (record != null)
            {
                if (record.DeviceId != deviceId)
                {
                    return new ChunkErrorOutcome(new DomainError(HttpErrorCode.CONFLICT,
                        "另一设备正在上传该文件", "该文件正在被其他设备上传，请稍后重试"));
                }
                // 同一设备：断点续传，重置数据（同时删除旧临时文件，避免旧数据污染合并结果）
                record.TotalChunks = totalChunks;
                record.FileHash = fileHash;
                record.BaseVersion = baseVersion;
                record.LastModified = lastModified ?? DateTime.UtcNow.ToString("O");
                record.ReceivedChunks = "[]";
                record.CreatedAt = DateTime.UtcNow.ToString("O");
                SafeDeleteTemp(record.TempPath);
            }
            else
            {
                // 创建临时文件
                string tempDir = Path.Combine(
                    Path.GetDirectoryName(_storage.GetAbsolutePath(path))!,
                    ".cloudpan");
                Directory.CreateDirectory(tempDir);
                string tempPath = Path.Combine(tempDir, $"{Guid.NewGuid():N}.chunk.tmp");

                record = new ChunkedUpload
                {
                    FilePath = path,
                    DeviceId = deviceId,
                    FileHash = fileHash,
                    TotalChunks = totalChunks,
                    ReceivedChunks = "[]",
                    TempPath = tempPath,
                    BaseVersion = baseVersion,
                    LastModified = lastModified ?? DateTime.UtcNow.ToString("O"),
                    CreatedAt = DateTime.UtcNow.ToString("O")
                };
                db.ChunkedUploads.Add(record);
            }
            await db.SaveChangesAsync();
        }
        else
        {
            record = await db.ChunkedUploads.FindAsync(path);
            if (record == null)
            {
                return new ChunkErrorOutcome(new DomainError(HttpErrorCode.BAD_REQUEST,
                    "分块上传会话不存在，请先传 chunkIndex=0", "分块上传会话已过期，请重新上传"));
            }

            if (record.TotalChunks != totalChunks)
            {
                return new ChunkErrorOutcome(new DomainError(HttpErrorCode.BAD_REQUEST,
                    "totalChunks 与首块不一致", "分块参数与首次上传不一致"));
            }

            if (!string.Equals(record.FileHash, fileHash, StringComparison.OrdinalIgnoreCase))
            {
                return new ChunkErrorOutcome(new DomainError(HttpErrorCode.BAD_REQUEST,
                    "fileHash 与首块不一致", "文件校验信息与首次上传不一致"));
            }
        }

        // 解析已接收块列表
        var received = JsonSerializer.Deserialize<List<int>>(record.ReceivedChunks)
                       ?? new List<int>();

        // 幂等：已接收则跳过
        if (received.Contains(chunkIndex))
        {
            return new ChunkProgressOutcome(path, chunkIndex, received.Count, totalChunks,
                received.Count == totalChunks);
        }

        // 写入块数据（追加到临时文件）
        await using (FileStream fs = new FileStream(record.TempPath, FileMode.Append, FileAccess.Write, FileShare.None))
        {
            await chunkContent.CopyToAsync(fs);
            await fs.FlushAsync();
        }

        received.Add(chunkIndex);
        record.ReceivedChunks = JsonSerializer.Serialize(received);
        await db.SaveChangesAsync();

        // 判断是否最后一块
        if (received.Count == totalChunks)
        {
            return await FinalizeAsync(db, record, path, fileHash, baseVersion, lastModified, deviceId);
        }

        return new ChunkProgressOutcome(path, chunkIndex, received.Count, totalChunks, false);
    }

    /// <inheritdoc />
    public async Task<ChunkStatusResult> GetStatusAsync(string path)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var record = await db.ChunkedUploads.FindAsync(path);

        if (record == null)
        {
            return new ChunkStatusResult(false, null, Array.Empty<int>(), 0, false, null, null);
        }

        var received = JsonSerializer.Deserialize<List<int>>(record.ReceivedChunks) ?? new List<int>();
        return new ChunkStatusResult(true, record.FilePath, received, record.TotalChunks,
            received.Count == record.TotalChunks, record.DeviceId, record.CreatedAt);
    }

    /// <summary>分块全部到达：合并校验、冲突检测、存档、原子写入、索引更新。</summary>
    private async Task<ChunkUploadOutcome> FinalizeAsync(
        CloudPanDbContext db, ChunkedUpload record, string path,
        string fileHash, int baseVersion, string? lastModified, string deviceId)
    {
        // a. 校验完整文件 SHA-256
        string actualHash = await _storage.ComputeHashAsync(record.TempPath);
        if (!string.Equals(actualHash, fileHash, StringComparison.OrdinalIgnoreCase))
        {
            SafeDeleteTemp(record.TempPath);
            db.ChunkedUploads.Remove(record);
            await db.SaveChangesAsync();
            return new ChunkErrorOutcome(new DomainError(HttpErrorCode.BAD_REQUEST,
                $"文件哈希校验失败。期望: {fileHash[..16]}..., 实际: {actualHash[..16]}...",
                "文件校验失败，请重新上传"));
        }

        // b. 冲突检测
        if (baseVersion > 0)
        {
            var existing = await _index.GetByPathAsync(path);
            if (existing != null && existing.Version > baseVersion)
            {
                // 保存冲突副本
                int conflictVersion = await _version.NextVersionAsync();
                string nameWithoutExt = Path.GetFileNameWithoutExtension(path);
                string ext = Path.GetExtension(path);
                string suffix = DateTime.Now.ToString("_冲突_yyyyMMdd_HHmmss");
                string conflictPath = (Path.GetDirectoryName(path)?.Replace('\\', '/') ?? "");
                if (!conflictPath.EndsWith('/') && !string.IsNullOrEmpty(conflictPath))
                {
                    conflictPath += "/";
                }

                conflictPath = conflictPath + nameWithoutExt + suffix + ext;
                if (!conflictPath.StartsWith('/'))
                {
                    conflictPath = "/" + conflictPath;
                }

                IOFile.Copy(record.TempPath, _storage.GetAbsolutePath(conflictPath), overwrite: true);
                string conflictHash = await _storage.ComputeHashAsync(_storage.GetAbsolutePath(conflictPath));
                long fileSize = new FileInfo(_storage.GetAbsolutePath(conflictPath)).Length;
                var conflictEntry = await _index.UpsertFileAsync(
                    conflictPath, FileType.File, conflictHash, fileSize,
                    lastModified ?? DateTime.UtcNow.ToString("O"), conflictVersion,
                    FileState.Conflict);

                SafeDeleteTemp(record.TempPath);
                db.ChunkedUploads.Remove(record);
                await db.SaveChangesAsync();

                // 审计日志（冲突）
                await _syncLog.LogAsync(path, SyncOperation.Upload, deviceId, LogResult.Conflict,
                    $"客户端 v{baseVersion} vs 服务端 v{existing.Version}，冲突副本: {conflictPath}");

                return new ChunkConflictOutcome(path, existing.Version, baseVersion, conflictPath);
            }
        }

        // c. 存档旧版本 + 分配版本号 + 原子写入
        //    顺序：FS 准备（存档/计算哈希，不覆盖目标）→ DB 事务（FileEntry + VersionRecord + 清理）→ 成功后原子移动
        //    DB 失败时目标文件保持原状可恢复；catch 清理孤儿存档（FS 副作用）

        // —— 阶段 1：FS 准备（不覆盖目标文件，DB 失败可恢复）——
        string? archivePath = null;
        var existingForArchive = await _index.GetByPathAsync(path);
        if (existingForArchive != null && existingForArchive.CurrentHash != null)
        {
            archivePath = await _storage.StoreVersionAsync(path, existingForArchive.Version);
        }

        int newVersion = await _version.NextVersionAsync();
        string targetPath = _storage.GetAbsolutePath(path);
        string? dir = Path.GetDirectoryName(targetPath);
        if (dir != null)
        {
            Directory.CreateDirectory(dir);
        }

        // 对临时文件计算哈希与大小（不依赖已覆盖的目标文件）
        string hash = await _storage.ComputeHashAsync(record.TempPath);
        long uploadFileSize = new FileInfo(record.TempPath).Length;

        // —— 阶段 2：DB 事务（同一 DbContext：FileEntry + VersionRecord + 清理 ChunkedUpload）——
        await using var tx = await db.Database.BeginTransactionAsync();
        try
        {
            if (archivePath != null)
            {
                db.VersionRecords.Add(new VersionRecord
                {
                    FilePath = path,
                    Version = existingForArchive!.Version,
                    Hash = existingForArchive.CurrentHash!,
                    Size = existingForArchive.CurrentSize,
                    StoragePath = archivePath,
                    Timestamp = DateTime.UtcNow.ToString("O"),
                    DeviceId = deviceId
                });

                // 保留最近 5 个版本
                var oldVersions = await db.VersionRecords
                    .Where(v => v.FilePath == path)
                    .OrderByDescending(v => v.Version)
                    .Skip(5)
                    .ToListAsync();
                db.VersionRecords.RemoveRange(oldVersions);
            }

            // 更新 FileEntry（同一 DbContext，避免 _index.UpsertFileAsync 独立上下文游离于事务外）
            var entry = await db.FileEntries.FindAsync(path);
            if (entry != null)
            {
                entry.CurrentHash = hash;
                entry.CurrentSize = uploadFileSize;
                entry.Version = newVersion;
                entry.LastModified = record.LastModified;
                entry.State = (int)FileState.Synced;
            }
            else
            {
                db.FileEntries.Add(new FileEntry
                {
                    Path = path,
                    Type = (int)FileType.File,
                    CurrentHash = hash,
                    CurrentSize = uploadFileSize,
                    Version = newVersion,
                    LastModified = record.LastModified,
                    State = (int)FileState.Synced,
                    CreatedAt = DateTime.UtcNow.ToString("O")
                });
            }

            // 清理 ChunkedUpload 记录
            db.ChunkedUploads.Remove(record);

            await db.SaveChangesAsync();
            await tx.CommitAsync();
        }
        catch
        {
            await tx.RollbackAsync();
            // DB 回滚后清理孤儿存档文件（FS 副作用）
            if (archivePath != null)
            {
                try
                {
                    IOFile.Delete(Path.Combine(
                        _storage.GetAbsolutePath("/"), ".cloudpan", ".versions", archivePath));
                }
                catch { /* 尽力清理 */ }
            }
            throw;
        }

        // —— 阶段 3：FS 原子覆盖（DB 已提交；失败时文件保持旧内容，下次同步按哈希重传自愈）——
        try
        {
            IOFile.Move(record.TempPath, targetPath, overwrite: true);
        }
        catch
        {
            try { SafeDeleteTemp(record.TempPath); } catch { }
            throw;
        }

        // 审计日志（FS 覆盖成功后才写入）
        await _syncLog.LogAsync(path, SyncOperation.Upload, deviceId, LogResult.Success);

        return new ChunkCompletedOutcome(path, newVersion, hash, uploadFileSize);
    }

    /// <summary>安全删除临时文件。</summary>
    private static void SafeDeleteTemp(string path)
    {
        try
        {
            if (IOFile.Exists(path))
            {
                IOFile.Delete(path);
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"删除临时文件失败: {path} - {ex.Message}"); }
    }
}
