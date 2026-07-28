using CloudPan.Shared;
using CloudPan.Client.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CloudPan.Client.Services;

/// <summary>
/// 同步引擎——客户端状态机核心。
/// 空闲 → 扫描变更 → 比对哈希 → 传输 → 应用变更 → 空闲
/// </summary>
public class SyncEngine
{
    private readonly IApiClient _api;
    private readonly string _syncRoot;
    private readonly IDbContextFactory<ClientDbContext> _dbFactory;
    private readonly ILogger<SyncEngine> _logger;

    // 队列优先级阈值（与 shared-spec.json config.queuePriorityThreshold 对齐）
    private const int QueuePriorityThreshold = 1_048_576; // 1MB

    private volatile bool _running;
    private volatile bool _paused;
    private int _queueCompleted;

    public event Action<string>? StatusChanged;
    public event Action<int, int>? QueueProgressChanged; // (completed, total)

    public SyncEngine(IApiClient api, Models.SyncConfig config, IDbContextFactory<ClientDbContext> dbFactory, ILogger<SyncEngine> logger)
    {
        _api = api;
        _syncRoot = config.SyncRoot;
        _dbFactory = dbFactory;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        _running = true;
        _logger.LogInformation("同步引擎启动");

        try
        {
            await FullSyncAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError($"首次同步失败（将继续尝试增量同步）: {ex.Message}");
        }

        while (_running && !ct.IsCancellationRequested)
        {
            if (!_paused)
            {
                try
                {
                    await ProcessQueueAsync(ct);
                    await IncrementalSyncAsync(ct);
                }
                catch (Exception ex)
                {
                    _logger.LogError($"同步周期异常: {ex.Message}");
                }
            }
            await Task.Delay(3000, ct);
        }
    }

    public void SetPaused(bool paused)
    {
        _paused = paused;
        NotifyStatus(paused ? "已暂停" : "运行中");
    }

    public void Stop() => _running = false;

    /// <summary>将本地文件变更加入上传队列。</summary>
    public async Task EnqueueLocalChangeAsync(string relativePath, SyncOperation operation)
    {
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
        if (existing != null) return;

        // 上传去重：文件大小与快照一致 → 大概率未变，跳过（Phase 0 简化策略）
        int fileSize = 0;
        if (operation == SyncOperation.Upload)
        {
            var fullPath = Path.Combine(_syncRoot, relativePath.TrimStart('/'));
            if (!File.Exists(fullPath)) return;

            var snapshot = await db.RemoteSnapshots.FindAsync(relativePath);
            var localSize = (int)new FileInfo(fullPath).Length;
            if (snapshot != null && localSize == snapshot.Size)
            {
                _logger.LogInformation($"跳过上传（大小未变）: {relativePath}");
                return;
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

    // ============================================================
    // 同步核心
    // ============================================================

    private async Task FullSyncAsync(CancellationToken ct)
    {
        NotifyStatus("首次全量同步...");
        _logger.LogInformation("开始全量同步");

        await using var db = await _dbFactory.CreateDbContextAsync();
        var cursor = await db.SyncCursor.FindAsync(1);
        var sinceVersion = cursor?.LastMaxVersion ?? 0;
        var maxVersion = sinceVersion;

        // 分页循环拉取全量文件树
        string? nextCursor = null;
        do
        {
            var response = await _api.GetFileTreeAsync(sinceVersion, cursor: nextCursor);
            if (response == null) break;

            await ApplyRemoteChangesAsync(db, response, ct);
            nextCursor = response.HasMore ? response.NextCursor : null;
            if (response.MaxVersion > maxVersion) maxVersion = response.MaxVersion;
        }
        while (nextCursor != null && !ct.IsCancellationRequested);

        // 更新游标（使用拉取开始前的版本号，确保正确性）
        if (cursor == null)
            db.SyncCursor.Add(new SyncCursorState { Id = 1, LastMaxVersion = maxVersion, LastSyncAt = DateTime.UtcNow.ToString("O") });
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
        var sinceVersion = cursor?.LastMaxVersion ?? 0;
        var maxVersion = sinceVersion;

        string? nextCursor = null;
        do
        {
            var response = await _api.GetFileTreeAsync(sinceVersion, cursor: nextCursor);
            if (response == null || response.Data.Count == 0) break;

            await ApplyRemoteChangesAsync(db, response, ct);
            nextCursor = response.HasMore ? response.NextCursor : null;
            if (response.MaxVersion > maxVersion) maxVersion = response.MaxVersion;
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
    private async Task ApplyRemoteChangesAsync(ClientDbContext db, FileTreeApiResponse response, CancellationToken ct)
    {
        foreach (var item in response.Data)
        {
            var localPath = ToLocalPath(item.Path);
            var snapshot = await db.RemoteSnapshots.FindAsync(item.Path);

            if (item.State == (int)FileState.Deleting)
            {
                if (File.Exists(localPath)) SafeDelete(localPath);
                if (snapshot != null) db.RemoteSnapshots.Remove(snapshot);
                _logger.LogInformation($"同步删除: {item.Path}");
                continue;
            }

            if (item.Type == (int)FileType.Directory)
            {
                // 目录：只更新快照，不下载
                if (snapshot == null)
                    db.RemoteSnapshots.Add(MakeSnapshot(item, item.State));
                else
                {
                    snapshot.Version = item.Version;
                    snapshot.State = item.State;
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
                    db.SyncQueue.Add(new SyncQueueItem
                    {
                        FilePath = item.Path,
                        Operation = (int)SyncOperation.Download,
                        Priority = (int)QueuePriority.Normal,
                        BaseVersion = item.Version
                    });
                }
            }

            // 快照更新移到下载成功后——此处仅记录快照创建，不更新版本号
            if (snapshot == null)
                db.RemoteSnapshots.Add(MakeSnapshot(item, item.State));
            // 版本/哈希/大小更新在 ProcessDownloadAsync 成功后执行
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
    // 传输队列处理
    // ============================================================

    private async Task ProcessQueueAsync(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var items = await db.SyncQueue
            .OrderByDescending(q => q.Priority)
            .ThenBy(q => q.CreatedAt)
            .Take(5)
            .ToListAsync();

        if (items.Count == 0) return;

        var remaining = await db.SyncQueue.CountAsync();
        QueueProgressChanged?.Invoke(_queueCompleted, remaining + _queueCompleted);

        foreach (var item in items)
        {
            if (ct.IsCancellationRequested) break;
            bool success = false;

            try
            {
                success = item.Operation switch
                {
                    (int)SyncOperation.Upload => await ProcessUploadAsync(item, ct),
                    (int)SyncOperation.Download => await ProcessDownloadAsync(item, ct),
                    (int)SyncOperation.Delete => await ProcessDeleteAsync(item, ct),
                    _ => false
                };
            }
            catch (Exception ex)
            {
                item.RetryCount++;
                item.LastError = ex.Message;
                _logger.LogError($"传输异常 [{item.RetryCount}/10]: {item.FilePath} — {ex.Message}");
            }

            if (success || item.RetryCount >= 10)
            {
                db.SyncQueue.Remove(item);
                _queueCompleted++;
                if (item.RetryCount >= 10)
                    _logger.LogError($"传输放弃（已达最大重试）: {item.FilePath}");
            }

            await db.SaveChangesAsync();
        }
    }

    /// <returns>true = 成功，应从队列移除</returns>
    private async Task<bool> ProcessUploadAsync(SyncQueueItem item, CancellationToken ct)
    {
        var localPath = ToLocalPath(item.FilePath);
        if (!File.Exists(localPath))
        {
            _logger.LogWarning($"上传跳过——文件不存在，移除队列项: {item.FilePath}");
            return true; // 文件已不存在，从队列移除
        }

        var lastModified = File.GetLastWriteTimeUtc(localPath).ToString("O");
        NotifyStatus($"上传: {item.FilePath}");

        var result = await _api.UploadAsync(localPath, item.FilePath, item.BaseVersion ?? 0, lastModified);
        _logger.LogInformation($"上传完成: {item.FilePath} → v{result?.Data.Version}");

        // 上传成功后更新本地快照，避免下次增量同步认为需要重新下载
        await using var db = await _dbFactory.CreateDbContextAsync();
        var snapshot = await db.RemoteSnapshots.FindAsync(item.FilePath);
        if (snapshot != null)
        {
            snapshot.Version = result?.Data.Version ?? snapshot.Version;
            snapshot.Hash = result?.Data.Hash ?? snapshot.Hash;
            snapshot.State = (int)CloudPan.Shared.FileState.Synced;
        }
        else if (result != null)
        {
            db.RemoteSnapshots.Add(new RemoteSnapshot
            {
                Path = item.FilePath,
                Type = (int)CloudPan.Shared.FileType.File,
                Hash = result.Data.Hash,
                Size = result.Data.Size,
                Version = result.Data.Version,
                State = (int)CloudPan.Shared.FileState.Synced
            });
        }
        await db.SaveChangesAsync();

        return true;
    }

    /// <returns>true = 成功，应从队列移除</returns>
    private async Task<bool> ProcessDownloadAsync(SyncQueueItem item, CancellationToken ct)
    {
        var localPath = ToLocalPath(item.FilePath);
        NotifyStatus($"下载: {item.FilePath}");

        var serverLastModified = await _api.DownloadAsync(item.FilePath, localPath);

        if (File.Exists(localPath) && serverLastModified != null
            && DateTime.TryParse(serverLastModified, out var dt))
        {
            File.SetLastWriteTimeUtc(localPath, dt);
        }

        // 下载成功后更新本地快照（延后更新，避免下载失败时幻同步）
        await using var db = await _dbFactory.CreateDbContextAsync();
        var snapshot = await db.RemoteSnapshots.FindAsync(item.FilePath);
        if (snapshot != null)
        {
            snapshot.Version = item.BaseVersion ?? snapshot.Version;
            snapshot.State = (int)CloudPan.Shared.FileState.Synced;
            await db.SaveChangesAsync();
        }

        _logger.LogInformation($"下载完成: {item.FilePath}");
        return true;
    }

    /// <returns>true = 成功，应从队列移除</returns>
    private async Task<bool> ProcessDeleteAsync(SyncQueueItem item, CancellationToken ct)
    {
        // 先调 API 删除服务端，成功后再删本地
        // 如果服务端返回 404（已删除），视为成功继续删本地
        try
        {
            await _api.DeleteAsync(item.FilePath, item.BaseVersion ?? 0);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // 服务端已不存在，继续删除本地即可
        }

        var localPath = ToLocalPath(item.FilePath);
        if (File.Exists(localPath))
        {
            SafeDelete(localPath);
            _logger.LogInformation($"本地删除: {item.FilePath}");
        }

        await using var db = await _dbFactory.CreateDbContextAsync();
        var snapshot = await db.RemoteSnapshots.FindAsync(item.FilePath);
        if (snapshot != null)
        {
            db.RemoteSnapshots.Remove(snapshot);
            await db.SaveChangesAsync();
        }

        return true;
    }

    // ============================================================
    // 工具方法
    // ============================================================

    /// <summary>
    /// 全量扫描本地文件，与远端快照对比，入队差异项。
    /// 作为 FileSystemWatcher 的兜底通道，每 5 分钟调用一次。
    /// </summary>
    public async Task FullScanAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("定时全量扫描开始...");
        NotifyStatus("全量扫描中...");

        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        // 1. 枚举本地所有文件（忽略 .cloudpan 和临时文件）
        var localFiles = new HashSet<string>();
        var localDirs = new HashSet<string>();

        if (Directory.Exists(_syncRoot))
        {
            foreach (var fullPath in Directory.EnumerateFiles(_syncRoot, "*", SearchOption.AllDirectories))
            {
                if (ShouldIgnoreScan(fullPath)) continue;
                var rel = ToRelativePath(fullPath);
                localFiles.Add(rel);
            }

            foreach (var fullPath in Directory.EnumerateDirectories(_syncRoot, "*", SearchOption.AllDirectories))
            {
                if (ShouldIgnoreScan(fullPath)) continue;
                var rel = ToRelativePath(fullPath);
                localDirs.Add(rel);
            }
        }

        // 2. 加载远端快照（字典 O(1) 查找，替代 O(n) FirstOrDefault）
        var snapshotDict = (await db.RemoteSnapshots.ToListAsync(ct))
            .ToDictionary(s => s.Path, s => s);

        // 3. 比对：新文件/修改文件 → 入队上传
        foreach (var path in localFiles)
        {
            var fullPath = ToLocalPath(path);

            if (!snapshotDict.TryGetValue(path, out var snapshot))
            {
                // 新文件
                await EnqueueLocalChangeAsync(path, SyncOperation.Upload);
                continue;
            }

            // 大小对比（Phase 0 简化策略，不计算哈希）
            var localSize = new FileInfo(fullPath).Length;
            if (localSize != snapshot.Size)
            {
                _logger.LogInformation("全量扫描检测到变更: {Path} ({OldSize} → {NewSize})",
                    path, snapshot.Size, localSize);
                await EnqueueLocalChangeAsync(path, SyncOperation.Upload);
            }
        }

        // 4. 比对：本地已删除 → 入队删除
        foreach (var (path, snapshot) in snapshotDict)
        {
            if (snapshot.Type == (int)CloudPan.Shared.FileType.File && !localFiles.Contains(path))
            {
                _logger.LogInformation("全量扫描检测到本地删除: {Path}", path);
                await EnqueueLocalChangeAsync(path, SyncOperation.Delete);
            }
        }

        _logger.LogInformation("全量扫描完成（文件: {FileCount}, 快照: {SnapshotCount}）",
            localFiles.Count, snapshotDict.Count);
    }

    private string ToRelativePath(string fullPath)
    {
        var relative = Path.GetRelativePath(_syncRoot, fullPath);
        return "/" + relative.Replace('\\', '/');
    }

    private bool ShouldIgnoreScan(string fullPath)
    {
        var fileName = Path.GetFileName(fullPath);
        return fileName.StartsWith('.')       // .cloudpan
            || fileName.EndsWith(".tmp")       // 临时文件
            || fullPath.Contains(".cloudpan"); // 内部元数据
    }

    private string ToLocalPath(string relativePath)
    {
        return Path.Combine(_syncRoot, relativePath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
    }

    private static void SafeDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }

    private void NotifyStatus(string status)
    {
        StatusChanged?.Invoke(status);
    }
}
