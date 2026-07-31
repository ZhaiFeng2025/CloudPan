using System.Collections.Concurrent;
using CloudPan.Client.Models;
using CloudPan.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CloudPan.Client.Services;

/// <summary>冲突详情。</summary>
public record ConflictInfo(
    string RelativePath,
    string LocalPath,
    DateTime? LocalModifiedTime,
    DateTime? RemoteModifiedTime,
    long LocalFileSize,
    long? RemoteFileSize,
    string? RemoteHash
);

/// <summary>同步引擎状态详情——包含阶段、文件级进度、字节级进度、当前传输文件和传输速率。</summary>
public record SyncStatus(
    string Phase,
    int CompletedFiles,
    int TotalFiles,
    string? CurrentFile,
    long BytesTransferred,
    long TotalBytes,
    long SpeedBytesPerSec,
    DateTime? LastSyncTime
);

/// <summary>冲突解决方式。</summary>
public enum ConflictResolution
{
    /// <summary>保留本地版本，覆盖服务端。</summary>
    KeepLocal,
    /// <summary>保留服务端版本，覆盖本地。</summary>
    KeepRemote,
    /// <summary>保留两者——本地文件重命名备份，下载服务端版本到原始路径。</summary>
    KeepBoth
}

/// <summary>
/// 同步引擎——客户端状态机核心。
/// 空闲 → 扫描变更 → 比对哈希 → 传输 → 应用变更 → 空闲
/// </summary>
public class SyncEngine : IDisposable
{
    private readonly IApiClient _api;
    private readonly string _syncRoot;
    private readonly IDbContextFactory<ClientDbContext> _dbFactory;
    private readonly ILogger<SyncEngine> _logger;
    private readonly FileWatcherService? _fileWatcher;
    private readonly WebSocketClient? _wsClient;

    // 队列优先级阈值（与 shared-spec.json config.queuePriorityThreshold 对齐）
    private const int QueuePriorityThreshold = 1_048_576; // 1MB

    // 跟踪字段
    private DateTime? _lastSyncTime;

    private volatile bool _running;
    private volatile bool _paused;

    // 文件级追踪（通过 Interlocked 安全读写）
    private int _queueCompleted;
    private long _completedBytes;
    private long _totalQueueBytes;
    private string? _currentFileName;
    private int _totalFileCount;

    // 速率估算
    private DateTime _lastRateTime = DateTime.UtcNow;
    private long _lastRateBytes;
    private double _currentRateBytesPerSecond;
    private bool _firstRateSample = true;

    // 增量同步并发锁（禁止多个 IncrementalSyncAsync 并发执行，防重复入队）
    private readonly SemaphoreSlim _syncLock = new(1, 1);

    // 5 分钟兜底全量扫描计时
    private DateTime _lastFullScan = DateTime.MinValue;
    private static readonly TimeSpan FullScanInterval = TimeSpan.FromMinutes(5);

    // 统一异常处理：区分 TaskCanceledException（HttpClient 超时）与 HttpRequestException
    private const int MaxRetryCount = 20;

    // 首次同步阶段标记（用于 FullScanAsync 状态文字区分）
    private bool _firstSyncActive;

    /// <summary>等待用户决策的冲突文件。Key 为相对路径。</summary>
    private readonly ConcurrentDictionary<string, ConflictInfo> _pendingConflicts = new();

    public event Action<string>? StatusChanged;
    public event Action<SyncStatus>? QueueProgressChanged;
    public event Action<ConflictInfo>? ConflictDetected;
    /// <summary>冲突已解决事件。参数为相对路径。</summary>
    public event Action<string>? ConflictResolved;
    /// <summary>同步错误事件。参数：(filePath, errorMessage, operationType)</summary>
    public event Action<string, string, SyncOperation>? ErrorOccurred;

    /// <summary>进度状态变更事件（详细信息）。</summary>
    /// <summary>最近一次完整同步完成的时间戳。</summary>
    public DateTime? LastSyncTime => _lastSyncTime;

    private readonly List<string> _selectedPaths;

    public SyncEngine(IApiClient api, Models.SyncConfig config, IDbContextFactory<ClientDbContext> dbFactory, ILogger<SyncEngine> logger, WebSocketClient? wsClient = null, FileWatcherService? fileWatcher = null)
    {
        _api = api;
        _syncRoot = config.SyncRoot;
        _dbFactory = dbFactory;
        _logger = logger;
        _fileWatcher = fileWatcher;
        _ignorePatterns = SyncIgnoreParser.LoadFromSyncRoot(_syncRoot);
        _selectedPaths = config.SelectedPaths ?? new List<string> { "/" };
        _wsClient = wsClient;

        // 订阅 WebSocket 推送事件（具名方法，Dispose 中取消订阅防止事件源持有本引擎引用导致泄漏）
        if (_wsClient != null)
        {
            _wsClient.OnFileChanged += OnWsFileChanged;
            _wsClient.OnFileDeleted += OnWsFileDeleted;
            _wsClient.OnFileRenamed += OnWsFileRenamed;
        }
    }

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
        TriggerWsIncrementalSync();
    }

    private void OnWsFileRenamed(string oldPath, string newPath)
    {
        _logger.LogInformation("WS 推送重命名: {OldPath} → {NewPath}", oldPath, newPath);
        TriggerWsIncrementalSync();
    }

    /// <summary>使用锁序列化增量同步调用，避免 WS 推送并发导致重复入队。</summary>
    private void TriggerWsIncrementalSync()
    {
        _ = Task.Run(async () =>
        {
            await _syncLock.WaitAsync();
            try { await IncrementalSyncAsync(CancellationToken.None); }
            catch (Exception ex) { _logger.LogWarning(ex, "WS 触发同步异常"); }
            finally { _syncLock.Release(); }
        });
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        _running = true;
        _logger.LogInformation("同步引擎启动");

        try
        {
            _firstSyncActive = true;
            // 首次同步：先下载所有远程文件，再上传本地变更
            NotifyStatus("首次同步 — 下载远程文件...");
            await FullSyncAsync(ct);
            // 优先处理下载队列，确保远程文件先同步到本地（防止 FullScanAsync 误判为"本地已删除"）
            NotifyStatus("首次同步 — 处理下载队列...");
            await DrainQueueAsync(ct);
            NotifyStatus("首次同步 — 扫描本地文件...");
            await FullScanAsync(ct);
            // 处理上传队列（本地新增/修改的变更）
            NotifyStatus("首次同步 — 处理上传队列...");
            await DrainQueueAsync(ct);
            _lastSyncTime = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            _logger.LogError($"首次同步失败（将继续尝试增量同步）: {ex.Message}");
        }
        finally
        {
            _firstSyncActive = false;
        }

        var lastHealthCheck = DateTime.MinValue;

        while (_running && !ct.IsCancellationRequested)
        {
            if (!_paused)
            {
                try
                {
                    await ProcessQueueAsync(ct);

                    // 增量同步（通过 _syncLock 防止 WebSocket 触发的并发调用重复入队）
                    if (await _syncLock.WaitAsync(0)) // 立即尝试，不等待
                    {
                        try { await IncrementalSyncAsync(ct); }
                        finally { _syncLock.Release(); }
                    }

                    // 5 分钟兜底全量扫描（FileSystemWatcher 遗漏补偿）
                    if ((DateTime.UtcNow - _lastFullScan) >= FullScanInterval)
                    {
                        _logger.LogInformation("定时全量扫描触发（间隔 {Interval}）", FullScanInterval);
                        await FullScanAsync(ct);
                        _lastFullScan = DateTime.UtcNow;
                    }

                    _lastSyncTime = DateTime.UtcNow;
                }
                catch (HttpRequestException)
                {
                    NotifyStatus("连接失败—等待重试");
                }
                catch (TaskCanceledException)
                {
                    NotifyStatus("连接超时—等待重试");
                }
                catch (Exception ex)
                {
                    _logger.LogError($"同步周期异常: {ex.Message}");
                }

                // 每 30 秒检测一次连接状态
                if ((DateTime.UtcNow - lastHealthCheck).TotalSeconds > 30)
                {
                    bool ok = await _api.HealthCheckAsync();
                    if (!ok)
                    {
                        NotifyStatus("离线—等待服务端连接");
                    }

                    lastHealthCheck = DateTime.UtcNow;
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

    public void Stop()
    {
        _running = false;
        _fileWatcher?.Dispose();
    }

    /// <summary>确保同步根目录存在，被删除时自动重建并记录警告。</summary>
    private void EnsureRootExists()
    {
        if (!Directory.Exists(NormalizePath(_syncRoot)))
        {
            _logger.LogWarning("同步根目录不存在，自动重建: {Path}", _syncRoot);
            Directory.CreateDirectory(NormalizePath(_syncRoot));
        }
    }

    /// <summary>将重命名操作入队。</summary>
    public async Task EnqueueRenameAsync(string oldPath, string newPath)
    {
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
            var response = await _api.GetFileTreeAsync(sinceVersion, cursor: nextCursor);
            if (response == null)
            {
                break;
            }

            await ApplyRemoteChangesAsync(db, response, ct);
            processedCount += response.Data.Count;
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
            var response = await _api.GetFileTreeAsync(sinceVersion, cursor: nextCursor);
            if (response == null || response.Data.Count == 0)
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
    private async Task ApplyRemoteChangesAsync(ClientDbContext db, FileTreeApiResponse response, CancellationToken ct)
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

        if (items.Count == 0)
        {
            return;
        }

        // 计算总队列字节数和文件数（每次重新计算，避免外部新增项导致进度倒缩）
        await RecalcQueueTotals(db);

        EmitProgress();

        // 定时进度报告（确保长传输期间至少每秒一次）
        using CancellationTokenSource progressCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task progressTask = Task.Run(async () =>
        {
            while (!progressCts.Token.IsCancellationRequested)
            {
                try { await Task.Delay(1000, progressCts.Token); EmitProgress(); }
                catch (OperationCanceledException) { /* 取消是预期的 */ }
                catch (Exception ex) { _logger.LogWarning(ex, "进度报告异常"); }
            }
        }, progressCts.Token);

        try
        {
            foreach (var item in items)
            {
                if (ct.IsCancellationRequested)
                {
                    break;
                }

                // 跳过等待用户决策的冲突文件
                if (_pendingConflicts.ContainsKey(item.FilePath))
                {
                    _logger.LogDebug("冲突文件跳过处理: {Path}", item.FilePath);
                    continue;
                }

                // 设置当前传输文件名
                _currentFileName = Path.GetFileName(item.FilePath);
                EmitProgress();

                bool success = false;

                try
                {
                    success = item.Operation switch
                    {
                        (int)SyncOperation.Upload => await ProcessUploadAsync(item, ct),
                        (int)SyncOperation.Download => await ProcessDownloadAsync(item, ct),
                        (int)SyncOperation.Delete => await ProcessDeleteAsync(item, ct),
                        (int)SyncOperation.Rename => await ProcessRenameAsync(item, ct),
                        _ => throw new InvalidOperationException("未知同步操作: " + item.Operation)
                    };
                }
                catch (Exception ex)
                {
                    item.RetryCount++;
                    item.LastError = ex.Message;
                    SyncOperation op = (SyncOperation)item.Operation;
                    _logger.LogError($"传输异常 [{item.RetryCount}/{MaxRetryCount}]: {item.FilePath} — {ex.Message}");
                    ErrorOccurred?.Invoke(item.FilePath, ex.Message, op);

                    // 阶梯退避：200ms→400ms→...→2000ms 后保持 2000ms
                    int backoffMs = GetBackoffDelay(item.RetryCount);
                    if (backoffMs > 0)
                    {
                        await Task.Delay(backoffMs, ct);
                    }
                }

                if (success || item.RetryCount >= MaxRetryCount)
                {
                    if (success && item.FileSize.HasValue)
                    {
                        Interlocked.Add(ref _completedBytes, item.FileSize.Value);
                    }

                    db.SyncQueue.Remove(item);
                    Interlocked.Increment(ref _queueCompleted);
                    if (item.RetryCount >= MaxRetryCount)
                    {
                        SyncOperation op = (SyncOperation)item.Operation;
                        _logger.LogError($"传输放弃（已达最大重试）: {item.FilePath}");
                        ErrorOccurred?.Invoke(item.FilePath, $"已达最大重试次数 ({item.RetryCount})", op);
                        NotifyStatus($"同步失败: {Path.GetFileName(item.FilePath)}");
                    }
                }

                await db.SaveChangesAsync();
                EmitProgress();
            }
        }
        finally
        {
            progressCts.Cancel();
            try { await progressTask; } catch (OperationCanceledException) { } catch (Exception ex) { _logger.LogWarning(ex, "等待进度任务完成时异常"); }
        }

        // 队列清空后清除当前文件名
        int remainingAfter = await db.SyncQueue.CountAsync();
        if (remainingAfter == 0)
        {
            _currentFileName = null;
            EmitProgress();
        }
    }

    /// <summary>从数据库重新计算队列总数和总字节数，避免外部新增项导致的进度倒缩。</summary>
    private async Task RecalcQueueTotals(ClientDbContext db)
    {
        int remaining = await db.SyncQueue.CountAsync();
        _totalFileCount = remaining + _queueCompleted;
        _totalQueueBytes = await db.SyncQueue.SumAsync(q => q.FileSize ?? 0);
    }

    /// <summary>
    /// 持续处理传输队列直到清空。用于首次同步阶段，确保下载队列处理完毕后再执行本地扫描，
    /// 防止 FullScanAsync 在本地文件尚未下载时误将远程文件判定为"已删除"。
    /// </summary>
    private async Task DrainQueueAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await ProcessQueueAsync(ct);
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            int remaining = await db.SyncQueue.CountAsync(ct);
            if (remaining == 0)
            {
                break;
            }

            await Task.Delay(200, ct);
        }
    }

    /// <summary>计算并发出当前进度状态。</summary>
    private void EmitProgress()
    {
        // 速率估算（跳过首次采样）
        if (!_firstRateSample)
        {
            var now = DateTime.UtcNow;
            double elapsed = (now - _lastRateTime).TotalSeconds;
            long deltaBytes = _completedBytes - _lastRateBytes;

            if (elapsed >= 1.0 && deltaBytes > 0)
            {
                _currentRateBytesPerSecond = deltaBytes / elapsed;
                _lastRateTime = now;
                _lastRateBytes = _completedBytes;
            }
            else if (elapsed >= 3.0)
            {
                // 超过 3 秒无数据传输，归零速率显示
                _currentRateBytesPerSecond = 0;
            }
        }
        else
        {
            _firstRateSample = false;
            _lastRateTime = DateTime.UtcNow;
            _lastRateBytes = _completedBytes;
        }

        SyncStatus status = new SyncStatus(
            Phase: _currentPhase ?? "同步中",
            CompletedFiles: _queueCompleted,
            TotalFiles: _totalFileCount,
            CurrentFile: _currentFileName,
            BytesTransferred: _completedBytes,
            TotalBytes: _totalQueueBytes + _completedBytes,
            SpeedBytesPerSec: (long)_currentRateBytesPerSecond,
            LastSyncTime: _lastSyncTime
        );

        QueueProgressChanged?.Invoke(status);
    }

    private string? _currentPhase;

    /// <returns>true = 成功，应从队列移除</returns>
    private async Task<bool> ProcessUploadAsync(SyncQueueItem item, CancellationToken ct)
    {
        string localPath = ToLocalPath(item.FilePath);
        if (!File.Exists(localPath))
        {
            _logger.LogWarning($"上传跳过——文件不存在，移除队列项: {item.FilePath}");
            return true; // 文件已不存在，从队列移除
        }

        string lastModified = File.GetLastWriteTimeUtc(localPath).ToString("O");
        NotifyStatus($"上传 ({_queueCompleted + 1}/{_totalFileCount}): {Path.GetFileName(item.FilePath)}");

        // m-08: 上传前记录文件 Hash，用于检测上传过程中文件是否被修改
        string? preUploadHash = null;
        try { preUploadHash = await ComputeSha256Async(localPath); }
        catch (Exception ex) { _logger.LogWarning(ex, "上传前计算文件哈希失败: {Path}", item.FilePath); }

        UploadApiResponse? result;
        try
        {
            result = await _api.UploadChunkedAsync(localPath, item.FilePath, item.BaseVersion ?? 0, lastModified);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            // 收集冲突详情
            var localModified = File.GetLastWriteTimeUtc(localPath);
            long localSize = new FileInfo(localPath).Length;

            string? remoteHash = null;
            long? remoteSize = null;
            try
            {
                await using var snapDb = await _dbFactory.CreateDbContextAsync(ct);
                var remoteSnapshot = await snapDb.RemoteSnapshots.FindAsync(new object[] { item.FilePath }, ct);
                if (remoteSnapshot != null)
                {
                    remoteHash = remoteSnapshot.Hash;
                    remoteSize = remoteSnapshot.Size;
                }
            }
            catch (Exception snapEx) { _logger.LogWarning(snapEx, "获取远程快照失败（非关键）"); }

            ConflictInfo conflictInfo = new ConflictInfo(
                RelativePath: item.FilePath,
                LocalPath: localPath,
                LocalModifiedTime: localModified,
                RemoteModifiedTime: null,
                LocalFileSize: localSize,
                RemoteFileSize: remoteSize,
                RemoteHash: remoteHash
            );

            _pendingConflicts.TryAdd(item.FilePath, conflictInfo);
            ConflictDetected?.Invoke(conflictInfo);
            _logger.LogWarning("上传冲突（409）: {Path} — 服务端版本已变更", item.FilePath);
            return false; // 队列项保留但被 _pendingConflicts 跳过，等待用户决策
        }
        _logger.LogInformation($"上传完成: {item.FilePath} → v{result?.Data.Version}");

        // m-08: 上传完成后再次读取 Hash，检测上传过程中文件是否被修改
        if (preUploadHash != null)
        {
            try
            {
                string postUploadHash = await ComputeSha256Async(localPath);
                if (!string.Equals(preUploadHash, postUploadHash, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning("上传过程中文件被修改，重新入队: {Path}", item.FilePath);
                    await EnqueueLocalChangeAsync(item.FilePath, SyncOperation.Upload);
                    return true; // 移除当前队列项，由新入队的项处理变更后的内容
                }
            }
            catch (FileNotFoundException)
            {
                // 上传后文件已被删除或改名 → 入队删除操作
                _logger.LogWarning("上传后文件已被删除，入队删除: {Path}", item.FilePath);
                await EnqueueLocalChangeAsync(item.FilePath, SyncOperation.Delete);
            }
            catch (Exception ex) { _logger.LogWarning(ex, "上传后计算文件哈希失败: {Path}", item.FilePath); }
        }

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
        string localPath = ToLocalPath(item.FilePath);

        // M-02: 下载前检测本地是否已修改
        if (File.Exists(localPath))
        {
            await using var checkDb = await _dbFactory.CreateDbContextAsync(ct);
            var snapshot = await checkDb.RemoteSnapshots.FindAsync(new object[] { item.FilePath }, ct);
            if (snapshot != null && !string.IsNullOrEmpty(snapshot.Hash))
            {
                string currentLocalHash = await ComputeSha256Async(localPath);
                if (!string.Equals(currentLocalHash, snapshot.Hash, StringComparison.OrdinalIgnoreCase))
                {
                    // 本地文件已被修改且未同步，触发冲突
                    var localModified = File.GetLastWriteTimeUtc(localPath);
                    long currentLocalSize = new FileInfo(localPath).Length;

                    ConflictInfo conflictInfo = new ConflictInfo(
                        RelativePath: item.FilePath,
                        LocalPath: localPath,
                        LocalModifiedTime: localModified,
                        RemoteModifiedTime: null,
                        LocalFileSize: currentLocalSize,
                        RemoteFileSize: item.FileSize,
                        RemoteHash: snapshot.Hash
                    );

                    _pendingConflicts.TryAdd(item.FilePath, conflictInfo);
                    ConflictDetected?.Invoke(conflictInfo);
                    _logger.LogWarning("下载前检测到本地修改（哈希不匹配），跳过下载: {Path}", item.FilePath);
                    return false; // 保留队列项，等待用户决策
                }
            }
        }

        // 下载前检查磁盘空间（大文件下载前确保有足够空间）
        if (item.FileSize.HasValue && item.FileSize.Value > 50_000_000)
        {
            try
            {
                DriveInfo drive = new DriveInfo(Path.GetPathRoot(_syncRoot)!);
                if (drive.AvailableFreeSpace < item.FileSize.Value + 50_000_000)
                {
                    _logger.LogWarning("磁盘空间不足，暂停大文件下载: {Path}（需要 {Need}MB，可用 {Avail}MB）",
                        item.FilePath, (item.FileSize.Value + 50_000_000) / 1_048_576, drive.AvailableFreeSpace / 1_048_576);
                    ErrorOccurred?.Invoke(item.FilePath, "磁盘空间不足，跳过下载", SyncOperation.Download);
                    return true; // 从队列移除，后续由全量扫描重新发现
                }
            }
            catch (Exception ex) { _logger.LogWarning(ex, "获取磁盘信息失败"); }
        }

        NotifyStatus($"下载 ({_queueCompleted + 1}/{_totalFileCount}): {Path.GetFileName(item.FilePath)}");

        var result = await _api.DownloadAsync(item.FilePath, localPath);

        // 1. 下载完成后检查文件是否存在
        if (!File.Exists(localPath))
        {
            _logger.LogWarning($"下载后文件不存在，重新入队: {item.FilePath}");
            await EnqueueDownloadAsync(item.FilePath, item.BaseVersion);
            return false; // 不标记成功，留在队列等待下次处理
        }

        // 2. 如果服务端返回了 X-File-Hash 头，计算本地文件哈希并比对
        if (!string.IsNullOrEmpty(result?.ExpectedHash))
        {
            string actualHash = await ComputeSha256Async(localPath);
            if (!string.Equals(actualHash, result.ExpectedHash, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    $"下载后哈希不匹配: {item.FilePath}（期望: {result.ExpectedHash[..16]}..., 实际: {actualHash[..16]}...），重新入队");
                await EnqueueDownloadAsync(item.FilePath, item.BaseVersion);
                return false;
            }
        }

        // 3. 设置服务端最后修改时间
        if (result?.LastModified != null && DateTime.TryParse(result.LastModified, out var dt))
        {
            File.SetLastWriteTimeUtc(localPath, dt);
        }

        // 下载成功后更新本地快照（延后更新，避免下载失败时幻同步）
        await using var db = await _dbFactory.CreateDbContextAsync();
        var dbSnapshot = await db.RemoteSnapshots.FindAsync(item.FilePath);

        // 获取下载后文件的实际哈希和大小（优先使用服务端返回的 ExpectedHash，避免重复计算）
        string downloadedHash;
        if (!string.IsNullOrEmpty(result?.ExpectedHash))
        {
            downloadedHash = result.ExpectedHash;
        }
        else
        {
            downloadedHash = await ComputeSha256Async(localPath);
        }

        long downloadedSize = new FileInfo(localPath).Length;

        if (dbSnapshot != null)
        {
            dbSnapshot.Version = item.BaseVersion ?? dbSnapshot.Version;
            dbSnapshot.Hash = downloadedHash;
            dbSnapshot.Size = downloadedSize;
            dbSnapshot.State = (int)CloudPan.Shared.FileState.Synced;
        }
        else
        {
            // 快照不存在时创建新快照（例如通过 DownloadPathAsync 手动触发的下载）
            db.RemoteSnapshots.Add(new RemoteSnapshot
            {
                Path = item.FilePath,
                Type = (int)CloudPan.Shared.FileType.File,
                Hash = downloadedHash,
                Size = downloadedSize,
                Version = item.BaseVersion ?? 0,
                State = (int)CloudPan.Shared.FileState.Synced
            });
        }
        await db.SaveChangesAsync();

        _logger.LogInformation($"下载完成: {item.FilePath}");
        return true;
    }

    /// <summary>将下载任务重新入队。</summary>
    private async Task EnqueueDownloadAsync(string filePath, int? baseVersion)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var existing = await db.SyncQueue
            .FirstOrDefaultAsync(q => q.FilePath == filePath && q.Operation == (int)SyncOperation.Download);
        if (existing != null)
        {
            return; // 已在队列中
        }

        db.SyncQueue.Add(new SyncQueueItem
        {
            FilePath = filePath,
            Operation = (int)SyncOperation.Download,
            Priority = (int)QueuePriority.Normal,
            BaseVersion = baseVersion ?? 0
        });
        await db.SaveChangesAsync();
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

        string localPath = ToLocalPath(item.FilePath);
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

    /// <returns>true = 成功</returns>
    private async Task<bool> ProcessRenameAsync(SyncQueueItem item, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(item.TargetPath))
        {
            _logger.LogWarning("重命名操作缺少目标路径: {Path}", item.FilePath);
            return true;
        }
        NotifyStatus($"重命名: {item.FilePath} → {item.TargetPath}");
        await _api.MoveAsync(item.FilePath, item.TargetPath, item.BaseVersion ?? 0);
        await using var db = await _dbFactory.CreateDbContextAsync();
        var snapshot = await db.RemoteSnapshots.FindAsync(item.FilePath);
        if (snapshot != null)
        {
            db.RemoteSnapshots.Remove(snapshot);
        }
        // 为新路径创建快照，避免下次全量扫描将新文件视为"新文件"重新上传
        db.RemoteSnapshots.Add(new RemoteSnapshot
        {
            Path = item.TargetPath,
            Type = snapshot?.Type ?? (int)CloudPan.Shared.FileType.File,
            Hash = snapshot?.Hash,
            Size = snapshot?.Size ?? 0,
            Version = item.BaseVersion ?? snapshot?.Version ?? 0,
            State = (int)CloudPan.Shared.FileState.Synced
        });
        await db.SaveChangesAsync();
        _logger.LogInformation("重命名完成: {Old} → {New}", item.FilePath, item.TargetPath);
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

    private readonly List<System.Text.RegularExpressions.Regex> _ignorePatterns;

    // 构造函数中初始化 _ignorePatterns（见上方构造函数修改）

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

    /// <summary>
    /// 指数退避延迟（毫秒）。带随机抖动，适用于网络/文件锁等瞬态错误。
    /// 第 1 次 ≈ 2s，第 2 次 ≈ 4s，第 3 次 ≈ 8s，...最大 30s。
    /// </summary>
    private static int GetBackoffDelay(int retryCount)
    {
        // 指数退避：2^retryCount * 1000ms，cap at 30s
        int exponential = Math.Min(1 << Math.Min(retryCount, 5), 30) * 1000;
        // 随机抖动 ±500ms
        int jitter = Random.Shared.Next(-500, 500);
        return Math.Max(exponential + jitter, 200);
    }

    private void SafeDelete(string path)
    {
        try { File.Delete(NormalizePath(path)); } catch (Exception ex) { _logger.LogWarning(ex, "删除文件失败: {Path}", path); }
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

    /// <summary>计算文件 SHA-256（64 字符十六进制）。</summary>
    private static async Task<string> ComputeSha256Async(string filePath)
    {
        using System.Security.Cryptography.SHA256 sha = System.Security.Cryptography.SHA256.Create();
        await using var stream = File.OpenRead(NormalizePath(filePath));
        byte[] hash = await sha.ComputeHashAsync(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>按需下载指定路径的文件（CloudOnly → 本地）。</summary>
    public async Task DownloadPathAsync(string path, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        db.SyncQueue.Add(new SyncQueueItem
        {
            FilePath = path,
            Operation = (int)SyncOperation.Download,
            Priority = (int)QueuePriority.High,
            CreatedAt = DateTime.UtcNow.ToString("O")
        });
        await db.SaveChangesAsync();
        _logger.LogInformation("按需下载入队: {Path}", path);
    }

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
                        // 备份失败 → 阻止下载，避免静默覆盖本地文件导致数据丢失
                        _logger.LogError(ex, "保留两者时重命名本地文件失败: {Path}", localPath);
                        ErrorOccurred?.Invoke(relativePath, $"本地文件备份失败，无法保留两者: {ex.Message}", SyncOperation.Download);
                        break;
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

    private void NotifyStatus(string status)
    {
        _currentPhase = status;
        StatusChanged?.Invoke(status);
        EmitProgress();
    }

    public void Dispose()
    {
        _running = false;
        // 取消 WebSocket 事件订阅，防止事件发布者持有本引擎引用导致泄漏
        if (_wsClient != null)
        {
            _wsClient.OnFileChanged -= OnWsFileChanged;
            _wsClient.OnFileDeleted -= OnWsFileDeleted;
            _wsClient.OnFileRenamed -= OnWsFileRenamed;
        }
        _syncLock.Dispose();
        _fileWatcher?.Dispose();
    }
}
