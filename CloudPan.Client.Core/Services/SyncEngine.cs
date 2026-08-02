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
public partial class SyncEngine : IDisposable
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

    private string? _currentPhase;

    private readonly List<System.Text.RegularExpressions.Regex> _ignorePatterns;

    // 构造函数中初始化 _ignorePatterns（见上方构造函数修改）

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
