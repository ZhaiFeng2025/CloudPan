using CloudPan.Contract;
using CloudPan.Infrastructure.Models;
using CloudPan.Infrastructure.Persistence.Client;
using Microsoft.Extensions.Logging;

namespace CloudPan.Client.Core.Services;

/// <summary>
/// 远程变更应用器（T-099 从 SyncEngine 拆分）：将服务端 /api/tree 拉取结果应用到本地——
/// 删除传播（Deleting 墓碑）、目录快照更新、CloudOnly 选择性同步、版本落后入队下载。
/// FullSync 与 IncrementalSync 共用，独立职责类承载以收敛 SyncEngine 聚合行数。
/// </summary>
internal sealed class SyncRemoteApplier
{
    private readonly ILogger _logger;
    private readonly SyncPathSelector _paths;
    private readonly string _syncRoot;

    public SyncRemoteApplier(ILogger logger, SyncPathSelector paths, string syncRoot)
    {
        _logger = logger;
        _paths = paths;
        _syncRoot = syncRoot;
    }

    /// <summary>将服务端的文件变更应用到本地——FullSync 和 IncrementalSync 共用。</summary>
    public async Task ApplyRemoteChangesAsync(IClientStore store, FileTreeResponse response, CancellationToken ct)
    {
        foreach (var item in response.Data)
        {
            string localPath = SyncPath.ToLocalPath(_syncRoot, item.Path);
            var snapshot = await store.GetSnapshotAsync(item.Path);

            if (item.State == (int)FileState.Deleting)
            {
                // 取消该路径待处理的上传/下载（远端已删除，本地未决传输不再有意义；与 WS file_deleted 处理一致）
                var pending = await store.GetQueuesByPathAsync(item.Path, new[] { (int)SyncOperation.Upload, (int)SyncOperation.Download });
                if (pending.Count > 0)
                {
                    store.RemoveQueueItems(pending);
                }

                if (File.Exists(localPath))
                {
                    SyncPath.SafeDelete(localPath, _logger);
                }
                else if (Directory.Exists(localPath))
                {
                    // T-049：目录墓碑——递归删除本地目录（含残留子项），避免空目录幽灵残留
                    // 并被下次 FullScan 当作『无快照本地目录』重新 mkdir 复活。
                    SafeDeleteDirectory(localPath);
                }

                if (snapshot != null)
                {
                    store.RemoveSnapshot(snapshot);
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
                    store.AddSnapshot(MakeSnapshot(item, item.State, isDownloaded: false));
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
            if (!_paths.IsPathSelected(item.Path))
            {
                if (snapshot == null)
                {
                    store.AddSnapshot(MakeSnapshot(item, (int)FileState.CloudOnly));
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
                    var existingDl = await store.GetQueueByPathAndOperationAsync(item.Path, (int)SyncOperation.Download);
                    if (existingDl == null)
                    {
                        store.AddQueueItem(new SyncQueue
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
                    var existingDl = await store.GetQueueByPathAndOperationAsync(item.Path, (int)SyncOperation.Download);
                    if (existingDl == null)
                    {
                        store.AddQueueItem(new SyncQueue
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
                    store.AddSnapshot(MakeSnapshot(item, item.State));
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

    /// <summary>递归删除本地目录（T-049：目录墓碑清理），尽力而为不抛异常。</summary>
    private void SafeDeleteDirectory(string path)
    {
        try
        {
            string normalized = SyncPath.NormalizePath(path);
            if (Directory.Exists(normalized))
            {
                Directory.Delete(normalized, recursive: true);
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "删除目录失败: {Path}", path); }
    }
}
