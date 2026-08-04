using CloudPan.Contract;
using CloudPan.Infrastructure.Models;
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

        await using var store = await _storeFactory.CreateStoreAsync();

        // 移除队列中所有对该文件的待处理操作，避免重复
        var pending = await store.GetQueuesByPathAsync(relativePath, null);
        store.RemoveQueueItems(pending);

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

                    store.AddQueueItem(new SyncQueue
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
                store.AddQueueItem(new SyncQueue
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
                        store.AddQueueItem(new SyncQueue
                        {
                            FilePath = relativePath,
                            Operation = (int)SyncOperation.Download,
                            Priority = (int)QueuePriority.High
                        });
                        await store.CommitAsync();
                        return;
                    }
                }

                store.AddQueueItem(new SyncQueue
                {
                    FilePath = relativePath,
                    Operation = (int)SyncOperation.Download,
                    Priority = (int)QueuePriority.High
                });
                _logger.LogInformation("冲突已解决 — 保留两者（下载服务端版本到原始路径）: {Path}", relativePath);
                break;
            }
            case ConflictResolution.ForceDelete:
            {
                // T-098：仍删除（强制）——仅删除冲突可选。baseVersion=0 不校验版本直接删除
                //（服务端 FileOperationService.DeleteAsync 仅当 baseVersion > 0 才做冲突检测），
                // 对齐 Android 弹窗『仍删除』语义，让 Windows 用户从冲突对话框完成删除意图。
                store.AddQueueItem(new SyncQueue
                {
                    FilePath = relativePath,
                    Operation = (int)SyncOperation.Delete,
                    Priority = (int)QueuePriority.High,
                    BaseVersion = 0
                });
                _logger.LogInformation("冲突已解决 — 仍删除（强制）: {Path}", relativePath);
                break;
            }
        }

        await store.CommitAsync();
        NotifyStatus("冲突已解决: " + relativePath);
        ConflictResolved?.Invoke(relativePath);
    }

    // T-084：删除 409 冲突——文件被其他设备修改/上传（服务端版本 > baseVersion）。
    // 转入冲突流程（_pendingConflicts + ConflictDetected），不静默丢弃删除意图、
    // 不删本地副本/快照，队列项保留等待用户决策（可『仍删除（强制）』或保留服务端版本撤销删除）。
    private async Task<bool> HandleDeleteConflictAsync(SyncQueue item)
    {
        string localPath = ToLocalPath(item.FilePath);

        // 本地文件信息读取——409 到达前本地文件可能已被其他路径删除/重命名（双删/重命名竞态）。
        // File.GetLastWriteTimeUtc 对不存在文件返回 MinValue 不抛异常，Length 读取才抛 FileNotFoundException。
        long localSize;
        try
        {
            localSize = new FileInfo(localPath).Length;
        }
        catch (FileNotFoundException)
        {
            // T-098：本地文件已不存在 → 跳过冲突入列（本地版本无可展示），
            // 按服务端 409 白话提示返回处理结果，不抛异常（CLAUDE.md 7.3 异常恢复路径）。
            _logger.LogWarning("删除冲突但本地文件已不存在（双删/重命名竞态），跳过冲突入列: {Path}", item.FilePath);
            ErrorOccurred?.Invoke(item.FilePath,
                new ErrorAttribution("文件已被其他设备修改", "请刷新后重试，确认是否需要删除该文件"), SyncOperation.Delete);
            return true; // 处理完成：本地已无文件，移除队列项（服务端因 409 保留）
        }

        // T-098：远程版本信息（RemoteHash/RemoteSize/RemoteModifiedTime）从本地快照填充，
        // 冲突对话框云盘版本显示真实值而非『未知』（对齐上传冲突 409 分支语义）。
        string? remoteHash = null;
        long? remoteSize = null;
        string? remoteLastModified = null;
        try
        {
            await using var snapStore = await _storeFactory.CreateStoreAsync();
            var snapshot = await snapStore.GetSnapshotAsync(item.FilePath);
            if (snapshot != null)
            {
                remoteHash = snapshot.Hash;
                remoteSize = snapshot.Size;
                remoteLastModified = snapshot.LastModified;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "获取删除冲突远程快照失败（非关键）");
        }

        ConflictInfo conflictInfo = new ConflictInfo(
            RelativePath: item.FilePath,
            LocalPath: localPath,
            LocalModifiedTime: File.GetLastWriteTimeUtc(localPath),
            RemoteModifiedTime: null,
            LocalFileSize: localSize,
            RemoteFileSize: remoteSize,
            RemoteHash: remoteHash,
            Operation: SyncOperation.Delete // T-098：标记删除冲突，UI 据此追加『仍删除（强制）』选项
        ) with { RemoteModifiedTime = ParseRemoteLastModified(remoteLastModified) };

        _pendingConflicts.TryAdd(item.FilePath, conflictInfo);
        ConflictDetected?.Invoke(conflictInfo);
        _logger.LogWarning("删除冲突（409）: {Path} — 文件已被其他设备修改，是否仍删除？", item.FilePath);
        return false; // 队列项保留但被 _pendingConflicts 跳过，等待用户决策
    }
}
