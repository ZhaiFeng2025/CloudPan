using CloudPan.Contract;
using CloudPan.Infrastructure.Models;
using CloudPan.Infrastructure.Persistence;
using CloudPan.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using IOFile = System.IO.File;

namespace CloudPan.Server.Core;

/// <summary>ChunkedUploadService 部分实现：全块到达后的合并校验、冲突检测、存档与原子写入（含失败回滚）。</summary>
public partial class ChunkedUploadService
{
    /// <summary>分块全部到达：合并校验、冲突检测、存档、原子写入、索引更新。</summary>
    private async Task<ChunkUploadOutcome> FinalizeAsync(
        CloudPanDbContext db, ChunkedUpload record, string path,
        string fileHash, int baseVersion, string? lastModified, string deviceId)
    {
        // a. 交叉校验：位图声称全块已收，但临时文件长度不足以容纳全部非末块（非末块均为满 ChunkSize）
        //    → 磁盘数据缺失（异常损坏），重置会话让客户端从头重传，避免合并后 SHA-256 永久失败
        long fileLength = new FileInfo(record.TempPath).Length;
        long minExpectedLength = (record.TotalChunks - 1) * (long)SpecConfig.ChunkSize;
        if (fileLength < minExpectedLength)
        {
            SafeDeleteTemp(record.TempPath);
            db.ChunkedUploads.Remove(record);
            await db.SaveChangesAsync();
            return new ChunkErrorOutcome(new DomainError(HttpErrorCode.BAD_REQUEST,
                "分块会话数据不完整", "上传会话已损坏，请重新上传"));
        }

        // b. 校验完整文件 SHA-256
        string actualHash = await FileHasher.ComputeSha256Async(record.TempPath);
        if (!string.Equals(actualHash, fileHash, StringComparison.OrdinalIgnoreCase))
        {
            SafeDeleteTemp(record.TempPath);
            db.ChunkedUploads.Remove(record);
            await db.SaveChangesAsync();
            return new ChunkErrorOutcome(new DomainError(HttpErrorCode.BAD_REQUEST,
                $"文件哈希校验失败。期望: {fileHash[..16]}..., 实际: {actualHash[..16]}...",
                "文件校验失败，请重新上传"));
        }

        // c. 冲突检测与冲突副本保存（单一辅助：检测 + 路径拼接 + 版本分配 + 原子写 + upsert + 审计）
        if (baseVersion > 0)
        {
            var existing = await _index.GetByPathAsync(path);
            await using var conflictStream = new FileStream(record.TempPath, FileMode.Open, FileAccess.Read);
            var conflict = await _conflictBackup.SaveConflictCopyIfNeededAsync(
                path, baseVersion, existing?.Version ?? 0, conflictStream,
                fileLength, record.LastModified, deviceId);
            if (conflict != null)
            {
                SafeDeleteTemp(record.TempPath);
                db.ChunkedUploads.Remove(record);
                await db.SaveChangesAsync();

                return new ChunkConflictOutcome(path, conflict.CurrentVersion, baseVersion, conflict.ConflictPath);
            }
        }

        // d. 存档旧版本 + 分配版本号 + 原子写入
        //    顺序：FS 准备（存档/计算哈希，不覆盖目标）→ DB 事务（FileEntry + VersionRecord + 清理）→ 成功后原子移动
        //    DB 失败时目标文件保持原状可恢复；孤儿存档清理统一由 VersionCommitHelper 承担（FS 副作用）

        // —— 阶段 1：FS 准备（不覆盖目标文件，DB 失败可恢复）——
        var existingForArchive = await _index.GetByPathAsync(path);
        string? archivePath = await _versionCommit.ArchiveOldVersionAsync(path, existingForArchive);

        int newVersion = await _version.NextVersionAsync();
        string targetPath = _storage.GetAbsolutePath(path);
        string? dir = Path.GetDirectoryName(targetPath);
        if (dir != null)
        {
            Directory.CreateDirectory(dir);
        }

        // 对临时文件计算哈希与大小（不依赖已覆盖的目标文件）
        string hash = await FileHasher.ComputeSha256Async(record.TempPath);
        long uploadFileSize = new FileInfo(record.TempPath).Length;

        // —— 阶段 2：DB 事务（同一 DbContext，经 VersionCommitHelper 单点提交）——
        //    辅助统一『存档记录 + 裁剪 + upsert FileEntry』的事务边界、回滚与孤儿存档清理
        //    标记 Finalized 与移除会话同事务提交：事务成功即代表 Finalize 完成（文件落盘在阶段 3，
        //    若阶段 2 提交后崩溃，会话已移除，索引指向新版本；客户端重传按哈希差异收敛，不丢上传）。
        await _versionCommit.CommitNewVersionInTransactionAsync(
            db, path, existingForArchive, archivePath,
            new VersionCommitState(path, hash, uploadFileSize, newVersion, record.LastModified),
            deviceId, prune: true,
            extraDbWork: () =>
            {
                record.Finalized = true;
                db.ChunkedUploads.Remove(record);
            });

        // —— 阶段 3：FS 原子覆盖（DB 已提交）——
        //    Move 失败（目标被锁等）时磁盘仍是旧内容，但索引已指向新 hash/version——若放任，客户端下轮树同步
        //    对齐错误索引（本地新内容匹配索引新 hash）永不重传，索引与磁盘永久不一致（F-21 毒化状态）。
        //    处理：回滚 FileEntry 到旧 hash/version（新建文件则删除条目）+ 移除孤儿 VersionRecord 与存档，
        //    使索引回到与磁盘一致的旧状态；客户端重试/下轮扫描按哈希差异重新上传收敛。
        try
        {
            IOFile.Move(record.TempPath, targetPath, overwrite: true);
        }
        catch
        {
            try { await RollbackFinalizeAsync(path, existingForArchive, archivePath); }
            finally { SafeDeleteTemp(record.TempPath); }
            throw;
        }

        // 审计日志（FS 覆盖成功后才写入）
        await _syncLog.LogAsync(path, SyncOperation.Upload, deviceId, LogResult.Success);

        return new ChunkCompletedOutcome(path, newVersion, hash, uploadFileSize);
    }

    /// <summary>
    /// Finalize 的 Move 覆盖目标失败（文件被锁等）后回滚索引（F-21 / CLAUDE.md 7.1 DB+FS 一致性）。
    /// 阶段 2 的 DB 事务已提交（索引指向新 hash/version），但 Move 失败磁盘仍是旧内容——若不回滚，
    /// 客户端下轮树同步对齐错误索引（本地新内容 == 索引新 hash）永不重传，索引与内容永久不一致。
    /// 委托 VersionCommitHelper.RollbackCommittedVersionAsync（版本编排单点，与 Restore 失败回滚同源不分叉）。
    /// </summary>
    private Task RollbackFinalizeAsync(string path, FileEntry? oldEntry, string? archivePath)
        => _versionCommit.RollbackCommittedVersionAsync(_dbFactory, path, oldEntry, archivePath);

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
