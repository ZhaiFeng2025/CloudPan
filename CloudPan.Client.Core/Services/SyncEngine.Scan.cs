using CloudPan.Contract;
using CloudPan.Infrastructure.Models;
using CloudPan.Infrastructure.Persistence.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CloudPan.Client.Core.Services;

/// <summary>SyncEngine 部分实现：全量/增量同步与远程变更应用（下载通道）。</summary>
public partial class SyncEngine
{
    // ============================================================
    // 同步核心
    // ============================================================

    private async Task FullSyncAsync(CancellationToken ct)
    {
        NotifyStatus("首次同步 — 下载远程文件...");
        _logger.LogInformation("开始全量同步");

        // 检查磁盘空间（低于 100MB 拒绝同步）
        try
        {
            DriveInfo drive = new DriveInfo(Path.GetPathRoot(_syncRoot)!);
            if (drive.AvailableFreeSpace < 100_000_000)
            {
                _logger.LogError("磁盘空间不足: {Available}MB，同步已暂停", drive.AvailableFreeSpace / 1_048_576);
                NotifyStatus("同步失败—磁盘空间不足 (可用 " + (drive.AvailableFreeSpace / 1_048_576) + " MB)");
                return;
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "获取磁盘信息失败"); }

        await using var db = await _dbFactory.CreateDbContextAsync();
        var cursor = await db.SyncCursor.FindAsync(1);
        int sinceVersion = cursor?.LastMaxVersion ?? 0;
        int maxVersion = sinceVersion;

        // 分页循环拉取全量文件树
        string? nextCursor = null;
        int processedCount = 0;
        do
        {
            var response = await _api.GetFileTreeAsync(sinceVersion, cursor: nextCursor, ct: ct);
            if (response == null)
            {
                break;
            }

            await ApplyRemoteChangesAsync(db, response, ct);
            processedCount += response.Data.Length;
            NotifyStatus($"首次同步 — 下载远程文件 ({processedCount} 项)");
            nextCursor = response.HasMore ? response.NextCursor : null;
            if (response.MaxVersion > maxVersion)
            {
                maxVersion = response.MaxVersion;
            }
        }
        while (nextCursor != null && !ct.IsCancellationRequested);

        // 更新游标（使用拉取开始前的版本号，确保正确性）
        if (cursor == null)
        {
            db.SyncCursor.Add(new SyncCursor { Id = 1, LastMaxVersion = maxVersion, LastSyncAt = DateTime.UtcNow.ToString("O") });
        }
        else
        {
            cursor.LastMaxVersion = maxVersion;
            cursor.LastSyncAt = DateTime.UtcNow.ToString("O");
        }

        await db.SaveChangesAsync();
        NotifyStatus("就绪");
    }

    private async Task IncrementalSyncAsync(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var cursor = await db.SyncCursor.FindAsync(1);
        int sinceVersion = cursor?.LastMaxVersion ?? 0;
        int maxVersion = sinceVersion;

        string? nextCursor = null;
        do
        {
            var response = await _api.GetFileTreeAsync(sinceVersion, cursor: nextCursor, ct: ct);
            if (response == null || response.Data.Length == 0)
            {
                break;
            }

            await ApplyRemoteChangesAsync(db, response, ct);
            nextCursor = response.HasMore ? response.NextCursor : null;
            if (response.MaxVersion > maxVersion)
            {
                maxVersion = response.MaxVersion;
            }
        }
        while (nextCursor != null && !ct.IsCancellationRequested);

        if (cursor != null)
        {
            if (maxVersion > cursor.LastMaxVersion)
            {
                cursor.LastMaxVersion = maxVersion;
                cursor.LastSyncAt = DateTime.UtcNow.ToString("O");
            }
        }
        else
        {
            // 游标不存在则创建（FullSyncAsync 失败后的恢复路径）
            db.SyncCursor.Add(new SyncCursor { Id = 1, LastMaxVersion = maxVersion, LastSyncAt = DateTime.UtcNow.ToString("O") });
        }

        await db.SaveChangesAsync();
    }

    // IncrementalSyncAsync ends here. The cursor creation fix applies to the IncrementalSyncAsync method only.
    // Note: this edit targets the cursor update block at the end of both FullSyncAsync and IncrementalSyncAsync.
    // FullSyncAsync already creates cursor if null, so the 'else' branch only triggers for IncrementalSyncAsync.

    /// <summary>将服务端的文件变更应用到本地——FullSync 和 IncrementalSync 共用。</summary>
    private async Task ApplyRemoteChangesAsync(ClientDbContext db, FileTreeResponse response, CancellationToken ct)
    {
        foreach (var item in response.Data)
        {
            string localPath = ToLocalPath(item.Path);
            var snapshot = await db.RemoteSnapshots.FindAsync(item.Path);

            if (item.State == (int)FileState.Deleting)
            {
                // 取消该路径待处理的上传/下载（远端已删除，本地未决传输不再有意义；与 WS file_deleted 处理一致）
                var pending = await db.SyncQueue
                    .Where(q => q.FilePath == item.Path
                        && (q.Operation == (int)SyncOperation.Upload || q.Operation == (int)SyncOperation.Download))
                    .ToListAsync();
                if (pending.Count > 0)
                {
                    db.SyncQueue.RemoveRange(pending);
                }

                if (File.Exists(localPath))
                {
                    SafeDelete(localPath);
                }
                else if (Directory.Exists(localPath))
                {
                    // T-049：目录墓碑——递归删除本地目录（含残留子项），避免空目录幽灵残留
                    // 并被下次 FullScan 当作『无快照本地目录』重新 mkdir 复活。
                    SafeDeleteDirectory(localPath);
                }

                if (snapshot != null)
                {
                    db.RemoteSnapshots.Remove(snapshot);
                }

                _logger.LogInformation($"同步删除: {item.Path}");
                continue;
            }

            if (item.Type == (int)FileType.Directory)
            {
                // 目录：只更新快照，不下载（目录无落盘概念）。
                // T-049：远端目录快照 IsDownloaded=false——IsDownloaded 在此语义为『目录曾在本机物化』：
                // 本机创建并同步的目录（ProcessMkdirAsync）为 true；远端目录未在本机物化，FullScan
                // 目录删除兜底据此不误判（否则空目录在首次同步未物化时被判定为本地删除 → 删除-重建振荡）。
                if (snapshot == null)
                {
                    db.RemoteSnapshots.Add(MakeSnapshot(item, item.State, isDownloaded: false));
                }
                else
                {
                    snapshot.Version = item.Version;
                    snapshot.State = item.State;
                    snapshot.LastModified = item.LastModified; // T-036：远程修改时间跟随快照
                }
                continue;
            }

            // 选择性同步：不在选中路径内的文件标记为 CloudOnly，不入下载队列（本地无副本，IsDownloaded 保持 false）
            if (!IsPathSelected(item.Path))
            {
                if (snapshot == null)
                {
                    db.RemoteSnapshots.Add(MakeSnapshot(item, (int)FileState.CloudOnly));
                }
                else
                {
                    snapshot.State = (int)FileState.CloudOnly;
                    snapshot.LastModified = item.LastModified; // T-036：远程修改时间跟随快照
                }

                continue;
            }

            // T-054：重新勾选已排除目录——CloudOnly 快照且远端版本相等（排除期间远端未变）时，
            // 版本相等分支此前直接跳过，导致快照永久卡 CloudOnly。此处恢复同步：
            // 本地存在 → 恢复 State（重置 CloudOnly → Synced）；本地缺失 → 入队下载（CloudOnly 从未落盘）。
            // 远端版本已变更（Version < item.Version）则落入下方常规分支（哈希不同→下载，哈希相同→更新版本号），
            // 本地残留副本滞后时不误恢复 Synced（避免陈旧内容覆盖新版本）。
            if (snapshot != null && snapshot.State == (int)FileState.CloudOnly && snapshot.Version == item.Version)
            {
                if (File.Exists(localPath))
                {
                    snapshot.State = item.State;
                    snapshot.IsDownloaded = true;
                    snapshot.LastModified = item.LastModified;
                    _logger.LogInformation($"重新勾选已排除目录，本地副本恢复同步: {item.Path}");
                }
                else
                {
                    // 去重：检查队列中是否已有同路径下载项
                    var existingDl = await db.SyncQueue
                        .FirstOrDefaultAsync(q => q.FilePath == item.Path
                            && q.Operation == (int)SyncOperation.Download);
                    if (existingDl == null)
                    {
                        db.SyncQueue.Add(new SyncQueue
                        {
                            FilePath = item.Path,
                            Operation = (int)SyncOperation.Download,
                            Priority = (int)QueuePriority.Normal,
                            BaseVersion = item.Version,
                            FileSize = item.CurrentSize
                        });
                    }
                    _logger.LogInformation($"重新勾选已排除目录，本地缺失，入队下载: {item.Path}");
                }
                continue;
            }

            // 文件：版本落后则入队下载（哈希相同则跳过下载只更新版本号）
            if (snapshot == null || snapshot.Version < item.Version)
            {
                if (snapshot != null && snapshot.Hash == item.CurrentHash)
                {
                    // 哈希相同 → 内容未变，直接更新版本号
                    snapshot.Version = item.Version;
                    snapshot.State = item.State;
                    snapshot.LastModified = item.LastModified; // T-036：远程修改时间跟随快照
                    _logger.LogInformation($"跳过下载（哈希未变）: {item.Path}");
                }
                else
                {
                    // 去重：检查队列中是否已有同路径下载项
                    var existingDl = await db.SyncQueue
                        .FirstOrDefaultAsync(q => q.FilePath == item.Path
                            && q.Operation == (int)SyncOperation.Download);
                    if (existingDl == null)
                    {
                        db.SyncQueue.Add(new SyncQueue
                        {
                            FilePath = item.Path,
                            Operation = (int)SyncOperation.Download,
                            Priority = (int)QueuePriority.Normal,
                            BaseVersion = item.Version,
                            FileSize = item.CurrentSize
                        });
                    }
            }

            // 快照更新移到下载成功后——此处仅记录快照创建，不更新版本号。
            // IsDownloaded=false：下载完成前不得视为「已落盘」，全量扫描据此不触发删除传播（T-037）。
            if (snapshot == null)
                {
                    db.RemoteSnapshots.Add(MakeSnapshot(item, item.State));
                }
                // 版本/哈希/大小/IsDownloaded 更新在 ProcessDownloadAsync 成功后执行
            }
        }
    }

    /// <summary>
    /// 构建远程快照。<paramref name="isDownloaded"/> 标记「本地是否已成功落盘」（T-037）：
    /// 下载开始前创建的快照为 false（远端新文件首次下载窗口），下载完成后由 ProcessDownloadAsync 置 true。
    /// </summary>
    private static RemoteSnapshot MakeSnapshot(FileEntryDto item, int state, bool isDownloaded = false) => new()
    {
        Path = item.Path,
        Type = item.Type,
        Hash = item.CurrentHash,
        Size = item.CurrentSize,
        Version = item.Version,
        State = state,
        LastModified = item.LastModified,
        IsDownloaded = isDownloaded
    };
}
