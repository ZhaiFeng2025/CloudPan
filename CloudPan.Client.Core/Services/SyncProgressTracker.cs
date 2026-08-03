using CloudPan.Infrastructure.Persistence.Client;

namespace CloudPan.Client.Core.Services;

/// <summary>
/// 同步进度跟踪器（T-081 拆分）：持有队列/字节级进度计数器、速率估算、当前阶段与上次同步时间，
/// 统一构建 SyncStatus 并广播进度事件。与拆分前 SyncEngine 内联实现行为一致：
/// long 字段经 Interlocked 读写，非原子字段经 _rateLock 保护，Emit 可从同步线程与进度任务线程并发调用。
/// </summary>
internal sealed class SyncProgressTracker
{
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

    private string? _currentPhase;
    private DateTime? _lastSyncTime;

    /// <summary>进度事件：每次 Emit 广播一条 SyncStatus（由 SyncEngine 转发给外部订阅者）。</summary>
    public event Action<SyncStatus>? QueueProgressChanged;

    /// <summary>最近一次完整同步完成的时间戳。</summary>
    public DateTime? LastSyncTime => _lastSyncTime;

    /// <summary>更新当前阶段文字（同步中/就绪等，供状态栏与进度显示）。</summary>
    public void SetPhase(string phase) => _currentPhase = phase;

    /// <summary>记录完整同步完成时间。</summary>
    public void SetLastSyncTime(DateTime value) => _lastSyncTime = value;

    /// <summary>更新当前传输文件名。</summary>
    public void SetCurrentFile(string? name) => _currentFileName = name;

    /// <summary>累计已传输字节数（单项上传成功时调用）。</summary>
    public void AddCompletedBytes(long bytes) => Interlocked.Add(ref _completedBytes, bytes);

    /// <summary>累计已完成文件数（队列项出队时调用）。</summary>
    public void IncrementCompleted() => Interlocked.Increment(ref _queueCompleted);

    /// <summary>写入队列总数与总字节数（由 RecalcTotals 或外部 DB 计算后调用）。</summary>
    public void SetTotals(int fileCount, long queueBytes)
    {
        _totalFileCount = fileCount;
        Interlocked.Exchange(ref _totalQueueBytes, queueBytes);
    }

    /// <summary>队列进度标签（如 "3/50"），供上传/下载状态文字使用。</summary>
    public string ProgressLabel() => $"{_queueCompleted + 1}/{_totalFileCount}";

    /// <summary>从数据库重新计算队列总数和总字节数，避免外部新增项导致的进度倒缩。</summary>
    public async Task RecalcTotals(IClientStore store)
    {
        var totals = await store.GetQueueTotalsAsync();
        SetTotals(totals.Count + _queueCompleted, totals.TotalBytes);
    }

    /// <summary>计算并发出当前进度状态（速率估算经 _rateLock 保护非原子类型的读写）。</summary>
    public void Emit()
    {
        long speedBps;
        lock (_rateLock)
        {
            if (!_firstRateSample)
            {
                var now = DateTime.UtcNow;
                double elapsed = (now - _lastRateTime).TotalSeconds;
                long deltaBytes = Interlocked.Read(ref _completedBytes) - _lastRateBytes;

                if (elapsed >= 1.0 && deltaBytes > 0)
                {
                    _currentRateBytesPerSecond = deltaBytes / elapsed;
                    _lastRateTime = now;
                    _lastRateBytes = Interlocked.Read(ref _completedBytes);
                }
                else if (elapsed >= 3.0)
                {
                    _currentRateBytesPerSecond = 0;
                }
            }
            else
            {
                _firstRateSample = false;
                _lastRateTime = DateTime.UtcNow;
                _lastRateBytes = Interlocked.Read(ref _completedBytes);
            }
            speedBps = (long)_currentRateBytesPerSecond;
        }

        long completedBytes = Interlocked.Read(ref _completedBytes);
        SyncStatus status = new SyncStatus(
            Phase: _currentPhase ?? "同步中",
            CompletedFiles: _queueCompleted,
            TotalFiles: _totalFileCount,
            CurrentFile: _currentFileName,
            BytesTransferred: completedBytes,
            TotalBytes: Interlocked.Read(ref _totalQueueBytes) + completedBytes,
            SpeedBytesPerSec: speedBps,
            LastSyncTime: _lastSyncTime
        );

        QueueProgressChanged?.Invoke(status);
    }
}
