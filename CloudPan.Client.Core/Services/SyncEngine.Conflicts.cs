using CloudPan.Client.Models;
using CloudPan.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CloudPan.Client.Services;

/// <summary>SyncEngine 部分实现：冲突处理。</summary>
public partial class SyncEngine
{
    /// <summary>
    /// 用户对冲突文件做出解决决策后调用。
    /// </summary>
    /// <param name="relativePath">冲突文件的相对路径。</param>
    /// <param name="resolution">解决方式。</param>
    public async Task OnConflictResolved(string relativePath, ConflictResolution resolution)
    {
        _pendingConflicts.TryRemove(relativePath, out _);

        string localPath = ToLocalPath(relativePath);

        await using var db = await _dbFactory.CreateDbContextAsync();

        // 移除队列中所有对该文件的待处理操作，避免重复
        var pending = await db.SyncQueue
            .Where(q => q.FilePath == relativePath)
            .ToListAsync();
        db.SyncQueue.RemoveRange(pending);

        switch (resolution)
        {
            case ConflictResolution.KeepLocal:
            {
                // 直接入队上传（绕过 EnqueueLocalChangeAsync 的大小检查——冲突场景下本地内容已变）
                long fileSize = 0;
                if (File.Exists(localPath))
                    {
                        fileSize = new FileInfo(localPath).Length;
                    }

                    db.SyncQueue.Add(new SyncQueueItem
                {
                    FilePath = relativePath,
                    Operation = (int)SyncOperation.Upload,
                    Priority = (int)QueuePriority.High,
                    FileSize = fileSize
                });
                _logger.LogInformation("冲突已解决 — 保留本地版本（重新上传）: {Path}", relativePath);
                break;
            }
            case ConflictResolution.KeepRemote:
            {
                db.SyncQueue.Add(new SyncQueueItem
                {
                    FilePath = relativePath,
                    Operation = (int)SyncOperation.Download,
                    Priority = (int)QueuePriority.High
                });
                _logger.LogInformation("冲突已解决 — 保留服务端版本（重新下载）: {Path}", relativePath);
                break;
            }
            case ConflictResolution.KeepBoth:
            {
                // 本地文件重命名备份，再下载服务端版本到原始路径
                if (File.Exists(localPath))
                {
                        string backupPath = NormalizePath(localPath + $".conflict.{DateTime.Now:yyyyMMddHHmmss}");
                    try
                    {
                        File.Move(localPath, backupPath);
                        _logger.LogInformation("冲突已解决 — 保留两者，本地文件备份至: {Backup}", backupPath);
                    }
                    catch (Exception ex)
                    {
                        // 备份失败 → 中止解决流程（不触发 ConflictResolved），重新入队下载。
                        // ProcessDownloadAsync 会在下载前再次检测本地修改并重建冲突状态，避免文件悬空。
                        _logger.LogError(ex, "保留两者时重命名本地文件失败: {Path}", localPath);
                        ErrorOccurred?.Invoke(relativePath, $"本地文件备份失败，无法保留两者: {ex.Message}", SyncOperation.Download);
                        db.SyncQueue.Add(new SyncQueueItem
                        {
                            FilePath = relativePath,
                            Operation = (int)SyncOperation.Download,
                            Priority = (int)QueuePriority.High
                        });
                        await db.SaveChangesAsync();
                        return;
                    }
                }

                db.SyncQueue.Add(new SyncQueueItem
                {
                    FilePath = relativePath,
                    Operation = (int)SyncOperation.Download,
                    Priority = (int)QueuePriority.High
                });
                _logger.LogInformation("冲突已解决 — 保留两者（下载服务端版本到原始路径）: {Path}", relativePath);
                break;
            }
        }

        await db.SaveChangesAsync();
        NotifyStatus("冲突已解决: " + relativePath);
        ConflictResolved?.Invoke(relativePath);
    }
}
