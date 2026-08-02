using CloudPan.Client.Models;
using CloudPan.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CloudPan.Client.Services;

/// <summary>SyncEngine 部分实现：WebSocket 变更事件与变更入队。</summary>
public partial class SyncEngine
{
    // ============================================================
    // WebSocket 推送事件处理（具名方法，供 Dispose 取消订阅）
    // ============================================================

    private void OnWsFileChanged(string path)
    {
        _logger.LogInformation("WS 推送触发增量同步: {Path}", path);
        TriggerWsIncrementalSync();
    }

    private void OnWsFileDeleted(string path)
    {
        _logger.LogInformation("WS 推送删除: {Path}", path);
        // 按 path 精确处理：直接删除本地副本（不再仅触发增量同步等待树墓碑）。
        // Task.Run 包裹异步删除，避免 async void 异常逃逸；最后兜底触发增量同步（目录删除需拉子树墓碑）。
        Task.Run(async () =>
        {
            try
            {
                await DeleteLocalCopyAsync(path);
                _logger.LogInformation("WS 删除已处理，本地副本已删: {Path}", path);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "WS 删除本地副本失败: {Path}", path);
            }
            finally
            {
                TriggerWsIncrementalSync();
            }
        });
    }

    /// <summary>删除本地副本 + 清理快照与待处理队列（WS file_deleted 精确处理与树墓碑共用）。</summary>
    private async Task DeleteLocalCopyAsync(string path)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        // 取消该路径待处理的上传/下载（远端已删除，本地未决传输不再有意义）
        var pending = await db.SyncQueue
            .Where(q => q.FilePath == path
                && (q.Operation == (int)SyncOperation.Upload || q.Operation == (int)SyncOperation.Download))
            .ToListAsync();
        if (pending.Count > 0)
        {
            db.SyncQueue.RemoveRange(pending);
        }

        string localPath = ToLocalPath(path);
        if (File.Exists(localPath))
        {
            SafeDelete(localPath);
        }

        var snapshot = await db.RemoteSnapshots.FindAsync(path);
        if (snapshot != null)
        {
            db.RemoteSnapshots.Remove(snapshot);
        }

        await db.SaveChangesAsync();
    }

    private void OnWsFileRenamed(string oldPath, string newPath)
    {
        _logger.LogInformation("WS 推送重命名: {OldPath} → {NewPath}", oldPath, newPath);
        TriggerWsIncrementalSync();
    }

    /// <summary>使用锁序列化增量同步调用，避免 WS 推送并发导致重复入队。</summary>
    private void TriggerWsIncrementalSync()
    {
        Task.Run(async () =>
        {
            try
            {
                await _syncLock.WaitAsync();
                try { await IncrementalSyncAsync(CancellationToken.None); }
                catch (Exception ex) { _logger.LogWarning(ex, "WS 触发同步异常"); }
                finally { _syncLock.Release(); }
            }
            catch (Exception ex) { _logger.LogError(ex, "WS 触发同步调度异常"); }
        });
    }

    /// <summary>将重命名操作入队。</summary>
    public async Task EnqueueRenameAsync(string oldPath, string newPath)
    {
        // 忽略规则匹配的路径（内置 *.tmp 等）：原子写入的 tmp→目标 重命名不应同步
        if (SyncIgnoreParser.ShouldIgnore(oldPath, _ignorePatterns)
            || SyncIgnoreParser.ShouldIgnore(newPath, _ignorePatterns))
        {
            _logger.LogDebug("忽略匹配忽略规则的重命名: {Old} → {New}", oldPath, newPath);
            return;
        }

        await using var db = await _dbFactory.CreateDbContextAsync();
        // 去重：同路径已有的重命名
        var existing = await db.SyncQueue
            .FirstOrDefaultAsync(q => q.FilePath == oldPath && q.Operation == (int)SyncOperation.Rename);
        if (existing != null) { existing.TargetPath = newPath; await db.SaveChangesAsync(); return; }

        db.SyncQueue.Add(new SyncQueueItem
        {
            FilePath = oldPath,
            Operation = (int)SyncOperation.Rename,
            Priority = (int)QueuePriority.High,
            TargetPath = newPath
        });
        await db.SaveChangesAsync();
        _logger.LogInformation("入队重命名: {Old} → {New}", oldPath, newPath);
    }

    /// <summary>将本地文件变更加入上传队列。</summary>
    public async Task EnqueueLocalChangeAsync(string relativePath, SyncOperation operation)
    {
        // 忽略规则匹配的路径（内置 *.tmp 等 + 用户 .syncignore）：原子写入的临时文件不应同步上传
        if (SyncIgnoreParser.ShouldIgnore(relativePath, _ignorePatterns))
        {
            _logger.LogDebug("忽略匹配忽略规则的变更: {Path}", relativePath);
            return;
        }

        await using var db = await _dbFactory.CreateDbContextAsync();

        // 如果是删除操作，取消同一文件待处理的上传/下载
        if (operation == SyncOperation.Delete)
        {
            var pending = await db.SyncQueue
                .Where(q => q.FilePath == relativePath
                    && (q.Operation == (int)SyncOperation.Upload || q.Operation == (int)SyncOperation.Download))
                .ToListAsync();
            db.SyncQueue.RemoveRange(pending);
        }

        // 去重：相同操作已在队列中
        var existing = await db.SyncQueue
            .FirstOrDefaultAsync(q => q.FilePath == relativePath && q.Operation == (int)operation);
        if (existing != null)
        {
            return;
        }

        // 上传去重：文件大小与快照一致 → 进一步比对哈希；均未变则跳过
        long fileSize = 0;
        if (operation == SyncOperation.Upload)
        {
            string fullPath = NormalizePath(Path.Combine(_syncRoot, relativePath.TrimStart('/')));
            if (!File.Exists(fullPath))
            {
                return;
            }

            var snapshot = await db.RemoteSnapshots.FindAsync(relativePath);
            long localSize = new FileInfo(fullPath).Length;
            if (snapshot != null && localSize == snapshot.Size)
            {
                // 大小相同，进一步比对哈希确认无变化
                if (!string.IsNullOrEmpty(snapshot.Hash))
                {
                    // 哈希比对：大小相同且哈希一致 → 真实变更
                    string localHash = await ComputeSha256Async(fullPath);
                    if (string.Equals(localHash, snapshot.Hash, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogInformation("跳过上传（大小和哈希均未变）: {Path}", relativePath);
                        return;
                    }
                    _logger.LogInformation("大小相同但哈希不同，仍需上传: {Path}", relativePath);
                }
                else
                {
                    // 快照无哈希记录（旧版数据库迁移场景），执行上传以确保内容一致
                    _logger.LogInformation("快照无哈希记录，执行上传以确保内容一致: {Path}", relativePath);
                }
            }
            fileSize = localSize;
        }

        db.SyncQueue.Add(new SyncQueueItem
        {
            FilePath = relativePath,
            Operation = (int)operation,
            Priority = fileSize < QueuePriorityThreshold ? (int)QueuePriority.High : (int)QueuePriority.Normal,
            FileSize = fileSize
        });
        await db.SaveChangesAsync();
        _logger.LogInformation($"入队: {operation} {relativePath}");
    }
}
