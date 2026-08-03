using System.Collections.Concurrent;
using System.Net;
using CloudPan.Client.Core.Models;
using CloudPan.Contract;
using CloudPan.Infrastructure.Persistence.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CloudPan.Client.Core.Services;

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
public partial class SyncEngine : IDisposable
{
    private readonly IApiClient _api;
    private readonly string _syncRoot;
    private readonly IDbContextFactory<ClientDbContext> _dbFactory;
    private readonly ILogger<SyncEngine> _logger;
    private readonly FileWatcherService? _fileWatcher;
    private readonly WebSocketClient? _wsClient;

    // 队列优先级阈值（单源：shared-spec.json → SpecConfig.QueuePriorityThreshold）
    private const int QueuePriorityThreshold = SpecConfig.QueuePriorityThreshold;

    // 跟踪字段
    private DateTime? _lastSyncTime;

    private volatile bool _running;
    private volatile bool _paused;

    // 文件级追踪（long 字段通过 Interlocked 安全读写，防止 x86 上撕裂）
    private int _queueCompleted;
    private long _completedBytes;
    private long _totalQueueBytes;
    private string? _currentFileName;
    private int _totalFileCount;

    // 速率估算（非原子类型通过 _rateLock 保护）
    private readonly object _rateLock = new();
    private DateTime _lastRateTime = DateTime.UtcNow;
    private long _lastRateBytes;
    private double _currentRateBytesPerSecond;
    private bool _firstRateSample = true;

    // 增量同步并发锁（禁止多个 IncrementalSyncAsync 并发执行，防重复入队）
    private readonly SemaphoreSlim _syncLock = new(1, 1);

    // 兜底全量扫描间隔（单源：shared-spec.json → SpecConfig.ScanIntervalMinutes）
    private DateTime _lastFullScan = DateTime.MinValue;
    private static readonly TimeSpan FullScanInterval = TimeSpan.FromMinutes(SpecConfig.ScanIntervalMinutes);

    // 统一异常处理：区分 TaskCanceledException（HttpClient 超时）与 HttpRequestException
    // 最大重试次数单源：shared-spec.json → SpecConfig.MaxRetryCount
    private const int MaxRetryCount = SpecConfig.MaxRetryCount;

    // 首次同步阶段标记（用于 FullScanAsync 状态文字区分）
    private bool _firstSyncActive;

    /// <summary>等待用户决策的冲突文件。Key 为相对路径。</summary>
    private readonly ConcurrentDictionary<string, ConflictInfo> _pendingConflicts = new();

    // 连续 401（认证失效）计数：达到阈值触发 ReconfigurationRequired 重配引导（F-34/T-034）。
    // 计数在同步线程（StartAsync 循环 + 队列消费）单线程访问，仍用 Interlocked 保证并发安全（CLAUDE.md 7.4）。
    private const int AuthFailureThreshold = 3;
    private int _consecutiveAuthFailures;

    public event Action<string>? StatusChanged;
    public event Action<SyncStatus>? QueueProgressChanged;
    public event Action<ConflictInfo>? ConflictDetected;
    /// <summary>冲突已解决事件。参数为相对路径。</summary>
    public event Action<string>? ConflictResolved;
    /// <summary>同步错误事件。参数：(filePath, 白话归因, operationType)，归因由 ErrorAttribution 生成。</summary>
    public event Action<string, ErrorAttribution, SyncOperation>? ErrorOccurred;
    /// <summary>
    /// 连续收到 401（Token 或服务端配置已变更）时触发，UI 据此提示重新配置并打开配置页（F-34/T-034）。
    /// 仅在计数恰好越过阈值时触发一次；成功后重置，下次故障可再次触发。
    /// </summary>
    public event Action? ReconfigurationRequired;

    /// <summary>进度状态变更事件（详细信息）。</summary>
    /// <summary>最近一次完整同步完成的时间戳。</summary>
    public DateTime? LastSyncTime => _lastSyncTime;

    // T-063：排除集运行时可变（引用替换实现热更新，非启动快照）。
    // 引用类型字段赋值原子，volatile 保证设置线程与同步线程间的可见性；内容不就地修改，只整体替换引用。
    private volatile List<string> _selectedPaths;

    private string? _currentPhase;

    private readonly List<System.Text.RegularExpressions.Regex> _ignorePatterns;

    // 构造函数中初始化 _ignorePatterns（见上方构造函数修改）

    public SyncEngine(IApiClient api, SyncConfig config, IDbContextFactory<ClientDbContext> dbFactory, ILogger<SyncEngine> logger, WebSocketClient? wsClient = null, FileWatcherService? fileWatcher = null)
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

    /// <summary>
    /// 运行时热更新排除集（T-063）：引用替换（非启动快照），方法返回后 IsPathSelected 立即读新值。
    /// 由 UI 在保存设置后调用（UI 线程）。异步部分（清除已排除路径的排队传输项 + 触发一次全量扫描）
    /// 以 Task.Run 承载并捕获全部异常（CLAUDE.md 7.2），无需重启客户端。
    /// </summary>
    public void UpdateSelectedPaths(List<string> selectedPaths)
    {
        // 引用替换：立即生效（后续 IsPathSelected / 扫描 / 入队拦截均读新值）
        _selectedPaths = selectedPaths ?? new List<string> { "/" };
        _logger.LogInformation("排除集热更新：{Count} 条选择路径即时生效", _selectedPaths.Count);

        // 异步收尾：清除已排除路径的排队传输项（取消勾选目录不再继续外传）+ 触发全量扫描让新选择落地
        _ = Task.Run(async () =>
        {
            try
            {
                await using var db = await _dbFactory.CreateDbContextAsync();
                var excluded = await db.SyncQueue
                    .Where(q => !IsPathSelected(q.FilePath))
                    .ToListAsync();
                if (excluded.Count > 0)
                {
                    db.SyncQueue.RemoveRange(excluded);
                    await db.SaveChangesAsync();
                    _logger.LogInformation("排除集热更新：移除 {Count} 个已排除路径的排队传输项", excluded.Count);
                }

                // 全量扫描内部以 _syncLock 互斥，与主循环/5 分钟定时器安全并发（FileWatcherService 同款入口）
                await FullScanAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "排除集热更新触发同步失败");
            }
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

                    // 5 分钟兜底全量扫描由 FileWatcherService 定时器统一触发（单通道，避免与主循环重复扫描）
                    _lastSyncTime = DateTime.UtcNow;

                    // 本周期正常结束 → 重置连续认证失败计数（下次 401 可再次触发重配引导）
                    TrackAuthFailure(false);
                }
                catch (HttpRequestException ex)
                {
                    NotifyStatus("连接失败—等待重试");
                    // 扫描路径（树查询等）的 401 在此收敛——服务端 Token/同步根已变更时不再静默离线
                    TrackAuthFailure(ex.StatusCode == HttpStatusCode.Unauthorized);
                }
                catch (TaskCanceledException)
                {
                    NotifyStatus("连接超时—等待重试");
                }
                catch (Exception ex)
                {
                    _logger.LogError($"同步周期异常: {ex.Message}");
                    TrackAuthFailure(false);
                }

                // 每 30 秒检测一次连接状态
                if ((DateTime.UtcNow - lastHealthCheck).TotalSeconds > 30)
                {
                    bool ok = await _api.HealthCheckAsync(ct);
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

    private void NotifyStatus(string status)
    {
        _currentPhase = status;
        StatusChanged?.Invoke(status);
        EmitProgress();
    }

    /// <summary>
    /// 累计连续认证失败（401）：是则计数，达到阈值触发 ReconfigurationRequired；否则（成功/非 401 错误）重置。
    /// 阈值触发后计数继续增长不再重复触发，待成功后重置才允许下一次触发。
    /// </summary>
    private void TrackAuthFailure(bool isAuthFailure)
    {
        if (isAuthFailure)
        {
            if (Interlocked.Increment(ref _consecutiveAuthFailures) == AuthFailureThreshold)
            {
                ReconfigurationRequired?.Invoke();
            }
        }
        else
        {
            Interlocked.Exchange(ref _consecutiveAuthFailures, 0);
        }
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
