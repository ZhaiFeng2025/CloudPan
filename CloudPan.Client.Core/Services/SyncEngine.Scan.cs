using CloudPan.Client.Models;
using CloudPan.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CloudPan.Client.Services;

/// <summary>SyncEngine 部分实现：全量/增量同步与本地扫描（兜底通道）。</summary>
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
            db.SyncCursor.Add(new SyncCursorState { Id = 1, LastMaxVersion = maxVersion, LastSyncAt = DateTime.UtcNow.ToString("O") });
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
            db.SyncCursor.Add(new SyncCursorState { Id = 1, LastMaxVersion = maxVersion, LastSyncAt = DateTime.UtcNow.ToString("O") });
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
                if (File.Exists(localPath))
                {
                    SafeDelete(localPath);
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
                // 目录：只更新快照，不下载
                if (snapshot == null)
                {
                    db.RemoteSnapshots.Add(MakeSnapshot(item, item.State));
                }
                else
                {
                    snapshot.Version = item.Version;
                    snapshot.State = item.State;
                }
                continue;
            }

            // 选择性同步：不在选中路径内的文件标记为 CloudOnly，不入下载队列
            if (!IsPathSelected(item.Path))
            {
                if (snapshot == null)
                {
                    db.RemoteSnapshots.Add(MakeSnapshot(item, (int)FileState.CloudOnly));
                }
                else
                {
                    snapshot.State = (int)FileState.CloudOnly;
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
                        db.SyncQueue.Add(new SyncQueueItem
                        {
                            FilePath = item.Path,
                            Operation = (int)SyncOperation.Download,
                            Priority = (int)QueuePriority.Normal,
                            BaseVersion = item.Version,
                            FileSize = item.CurrentSize
                        });
                    }
            }

            // 快照更新移到下载成功后——此处仅记录快照创建，不更新版本号
            if (snapshot == null)
                {
                    db.RemoteSnapshots.Add(MakeSnapshot(item, item.State));
                }
                // 版本/哈希/大小更新在 ProcessDownloadAsync 成功后执行
            }
        }
    }

    private static RemoteSnapshot MakeSnapshot(FileEntryDto item, int state) => new()
    {
        Path = item.Path,
        Type = item.Type,
        Hash = item.CurrentHash,
        Size = item.CurrentSize,
        Version = item.Version,
        State = state
    };

    // ============================================================
    // 工具方法
    // ============================================================

    /// <summary>
    /// 全量扫描本地文件，与远端快照对比，入队差异项。
    /// 作为 FileSystemWatcher 的兜底通道，每 5 分钟调用一次。
    /// </summary>
    public async Task FullScanAsync(CancellationToken ct = default)
    {
        // 并发控制：与增量同步/WS 触发扫描互斥，避免并发重复入队（FileWatcher 定时器与主循环共用此入口）
        if (!await _syncLock.WaitAsync(0))
        {
            _logger.LogDebug("全量扫描跳过——已有同步任务在运行");
            return;
        }

        try
        {
            await FullScanCoreAsync(ct);
        }
        finally
        {
            _syncLock.Release();
        }
    }

    /// <summary>全量扫描核心逻辑（由 FullScanAsync 持锁调用）。</summary>
    private async Task FullScanCoreAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("定时全量扫描开始...");
        NotifyStatus("全量扫描中...");

        EnsureRootExists();

        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        // 1. 枚举本地所有文件及目录（忽略 .cloudpan 和临时文件），单次遍历替代原先两次独立遍历
        HashSet<string> localFiles = new HashSet<string>();

        if (Directory.Exists(NormalizePath(_syncRoot)))
        {
            foreach (string fullPath in Directory.EnumerateFileSystemEntries(NormalizePath(_syncRoot), "*", SearchOption.AllDirectories))
            {
                if (ShouldIgnoreScan(fullPath))
                {
                    continue;
                }

                if (Directory.Exists(fullPath))
                {
                    continue; // 只处理文件，跳过目录
                }

                string rel = ToRelativePath(fullPath);
                localFiles.Add(rel);
            }
        }

        // 2. 分批加载远端快照（每次 1000 条），避免全量加载到内存
        const int batchSize = 1000;
        HashSet<string> matchedLocalFiles = new HashSet<string>();
        int snapshotCount = 0;

        List<RemoteSnapshot> batch;
        do
        {
            batch = await db.RemoteSnapshots
                .OrderBy(s => s.Path)
                .Skip(snapshotCount)
                .Take(batchSize)
                .ToListAsync(ct);

            foreach (var snapshot in batch)
            {
                // 跳过 CloudOnly 文件（不含本地副本，不纳入删除检测，由用户按需下载）
                if (snapshot.State == (int)CloudPan.Shared.FileState.CloudOnly)
                {
                    continue;
                }

                if (!localFiles.Contains(snapshot.Path))
                {
                    // 本地已删除的文件 → 入队删除
                    if (snapshot.Type == (int)CloudPan.Shared.FileType.File)
                    {
                        _logger.LogInformation("全量扫描检测到本地删除: {Path}", snapshot.Path);
                        await EnqueueLocalChangeAsync(snapshot.Path, SyncOperation.Delete);
                    }
                    continue;
                }

                matchedLocalFiles.Add(snapshot.Path);

                // 文件：大小对比 + 哈希对比
                if (snapshot.Type == (int)CloudPan.Shared.FileType.File)
                {
                    string fullPath = ToLocalPath(snapshot.Path);
                    long localSize = new FileInfo(fullPath).Length;
                    if (localSize != snapshot.Size)
                    {
                        _logger.LogInformation("全量扫描检测到变更: {Path} ({OldSize} → {NewSize})",
                            snapshot.Path, snapshot.Size, localSize);
                        await EnqueueLocalChangeAsync(snapshot.Path, SyncOperation.Upload);
                    }
                    else if (!string.IsNullOrEmpty(snapshot.Hash))
                    {
                        // 大小相同，进一步比对哈希确认无变化
                        string localHash = await ComputeSha256Async(fullPath);
                        if (!string.Equals(localHash, snapshot.Hash, StringComparison.OrdinalIgnoreCase))
                        {
                            _logger.LogInformation("全量扫描检测到变更（哈希不同）: {Path}", snapshot.Path);
                            await EnqueueLocalChangeAsync(snapshot.Path, SyncOperation.Upload);
                        }
                    }
                }
            }

            snapshotCount += batch.Count;
        }
        while (batch.Count == batchSize && !ct.IsCancellationRequested);

        // 3. 无快照的本地文件 → 新文件，入队上传
        foreach (string path in localFiles)
        {
            if (matchedLocalFiles.Contains(path))
            {
                continue;
            }

            await EnqueueLocalChangeAsync(path, SyncOperation.Upload);
        }

        _logger.LogInformation("全量扫描完成（文件: {FileCount}, 快照: {SnapshotCount}）",
            localFiles.Count, snapshotCount);
        if (_firstSyncActive)
        {
            NotifyStatus($"全量扫描本地文件 — {localFiles.Count} 项");
        }
        else
        {
            NotifyStatus($"全量扫描完成 — {localFiles.Count} 项本地文件");
        }
    }

    private string ToRelativePath(string fullPath)
    {
        // 去除 \\?\ 前缀（如有），确保与 _syncRoot 格式一致
        string cleanFull = fullPath.StartsWith(@"\\?\") ? fullPath[4..] : fullPath;
        string cleanRoot = _syncRoot.StartsWith(@"\\?\") ? _syncRoot[4..] : _syncRoot;
        string relative = Path.GetRelativePath(cleanRoot, cleanFull);
        return "/" + relative.Replace('\\', '/');
    }

    private bool ShouldIgnoreScan(string fullPath)
    {
        string relativePath = ToRelativePath(fullPath);
        return SyncIgnoreParser.ShouldIgnore(relativePath, _ignorePatterns);
    }

    private string ToLocalPath(string relativePath)
    {
        string path = Path.Combine(_syncRoot, relativePath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
        return NormalizePath(path);
    }

    /// <summary>
    /// 为路径添加 \\?\ 前缀以支持长路径（超过 MAX_PATH 260 字符）。
    /// 对支持的所有文件 I/O 操作使用此方法包装路径。
    /// </summary>
    private static string NormalizePath(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return path;
        }
        // 已包含 \\?\ 前缀则跳过
        if (path.StartsWith(@"\\?\"))
        {
            return path;
        }
        // 只对绝对本地路径（如 C:\...）添加前缀
        if (path.Length >= 3 && path[1] == ':' && path[2] == '\\')
        {
            return @"\\?\" + path;
        }
        // UNC 路径（\\server\share）转换为 \\?\UNC\ 格式
        if (path.StartsWith(@"\\"))
        {
            return @"\\?\UNC\" + path[2..];
        }

        return path;
    }

    /// <summary>检查路径是否在已选择的同步范围内。</summary>
    private bool IsPathSelected(string path)
    {
        if (_selectedPaths.Count == 1 && _selectedPaths[0] == "/")
        {
            return true; // 全选
        }

        string normalized = path.TrimEnd('/') + "/";
        return _selectedPaths.Any(sp =>
        {
            string p = sp.TrimEnd('/') + "/";
            return normalized.StartsWith(p, StringComparison.OrdinalIgnoreCase)
                   || path.Equals(sp.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
        });
    }
}
