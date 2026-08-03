using CloudPan.Client.Core.Models;
using CloudPan.Contract;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CloudPan.Client.Core.Services;

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
                        // F-31：不再透出原始异常字符串
                        ErrorOccurred?.Invoke(relativePath, new ErrorAttribution("本地文件备份失败，无法保留两者", "请关闭可能占用该文件的程序后重试"), SyncOperation.Download);
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

    /// <summary>
    /// 下载服务端当前版本到临时目录，返回临时文件路径（用于冲突解决时的「打开两版本对比」）。
    /// 下载失败或服务端无此文件返回 null。
    /// </summary>
    public async Task<string?> DownloadRemoteToTempAsync(string relativePath, CancellationToken ct = default)
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "CloudPanCompare");
        Directory.CreateDirectory(tempDir);
        string ext = Path.GetExtension(relativePath);
        string tempPath = Path.Combine(tempDir,
            $"{Path.GetFileNameWithoutExtension(relativePath)}.remote{DateTime.Now:yyyyMMddHHmmss}{ext}");

        var result = await _api.DownloadAsync(relativePath, tempPath, ct: ct);
        if (result == null || !File.Exists(tempPath))
        {
            return null;
        }
        _logger.LogInformation("已下载服务端版本到临时文件供对比: {Path} → {Temp}", relativePath, tempPath);
        return tempPath;
    }
}
