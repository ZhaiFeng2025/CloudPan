using CloudPan.Contract;
using CloudPan.Infrastructure.Persistence;
using CloudPan.Server.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
// Host 是 WinForms 项目，隐式 using 含 System.Windows.Forms.Timer，用别名消除歧义
using Timer = System.Threading.Timer;

namespace CloudPan.Server.Host.Hosting;

/// <summary>
/// 后台定时任务宿主：回收站 30 天清理、墓碑物理清理、超时分块清理、WAL checkpoint、内存监控、统一存储回收。
/// 替换 Program.cs 中的裸 Timer（R-A6：定时任务用 IHostedService）。Timer 回调使用 Task.Run 包裹并捕获全部异常（CLAUDE.md 7.2）。
/// </summary>
public sealed class BackgroundHostedService : IHostedService, IDisposable
{
    private readonly IServiceProvider _services;
    private readonly ILogger<BackgroundHostedService> _logger;
    private Timer? _trashTimer;
    private Timer? _tombstoneTimer;
    private Timer? _chunkTimer;
    private Timer? _walTimer;
    private Timer? _memTimer;
    private Timer? _reclaimTimer;

    /// <summary>墓碑保留窗口（天）。与回收站 30 天清理对齐，保证客户端有足够时间同步删除。</summary>
    private static readonly TimeSpan TombstoneRetention = TimeSpan.FromDays(30);

    /// <summary>回收站保留窗口（天）。清理策略归属 TrashService（T-026），组合根只传保留期。</summary>
    private static readonly TimeSpan TrashRetention = TimeSpan.FromDays(30);

    /// <summary>缩略图缓存保留窗口（天）。过期缓存由统一存储回收任务清理（重建成本低）。保留期读 SpecConfig。</summary>
    private static readonly TimeSpan ThumbnailCacheRetention = TimeSpan.FromDays(SpecConfig.ThumbnailCacheRetentionDays);

    public BackgroundHostedService(IServiceProvider services, ILogger<BackgroundHostedService> logger)
    {
        _services = services;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _trashTimer = new Timer(TrashCleanup, null, TimeSpan.FromMinutes(10), TimeSpan.FromHours(6));
        _tombstoneTimer = new Timer(TombstoneCleanup, null, TimeSpan.FromMinutes(15), TimeSpan.FromHours(6));
        _chunkTimer = new Timer(ChunkCleanup, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30));
        _walTimer = new Timer(WalCheckpoint, null, TimeSpan.FromMinutes(60), TimeSpan.FromMinutes(60));
        _memTimer = new Timer(MemoryMonitor, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(10));
        // 统一存储回收（T-088）：间隔读 SpecConfig.StorageReclaimIntervalMinutes，首次延迟与周期一致
        _reclaimTimer = new Timer(StorageReclaim, null,
            TimeSpan.FromMinutes(SpecConfig.StorageReclaimIntervalMinutes),
            TimeSpan.FromMinutes(SpecConfig.StorageReclaimIntervalMinutes));
        _logger.LogInformation("后台定时任务已启动（回收站/墓碑/分块/WAL/内存监控/存储回收）");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _trashTimer?.Dispose();
        _tombstoneTimer?.Dispose();
        _chunkTimer?.Dispose();
        _walTimer?.Dispose();
        _memTimer?.Dispose();
        _reclaimTimer?.Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _trashTimer?.Dispose();
        _tombstoneTimer?.Dispose();
        _chunkTimer?.Dispose();
        _walTimer?.Dispose();
        _memTimer?.Dispose();
        _reclaimTimer?.Dispose();
    }

    // ==================== 回收站 30 天自动清理（策略在 TrashService，本处仅调度） ====================

    private void TrashCleanup(object? state)
    {
        Task.Run(async () =>
        {
            try
            {
                var trash = _services.GetRequiredService<ITrashService>();
                int purged = await trash.PurgeExpiredAsync(TrashRetention);
                if (purged > 0)
                {
                    _logger.LogInformation("清理过期回收站条目: {Count} 条", purged);
                }
            }
            catch (Exception ex) { _logger.LogWarning(ex, "回收站定时清理异常"); }
        });
    }

    // ==================== 墓碑物理清理（>30 天，客户端同步删除传播完成后可回收） ====================

    private void TombstoneCleanup(object? state)
    {
        Task.Run(async () =>
        {
            try
            {
                var index = _services.GetRequiredService<IFileIndexService>();
                int purged = await index.PurgeExpiredTombstonesAsync(DateTime.UtcNow - TombstoneRetention);
                if (purged > 0)
                {
                    _logger.LogInformation("清理过期墓碑: {Count} 条", purged);
                }
            }
            catch (Exception ex) { _logger.LogWarning(ex, "墓碑定时清理异常"); }
        });
    }

    // ==================== 分块上传超时清理（>24h 未完成） ====================

    private void ChunkCleanup(object? state)
    {
        Task.Run(async () =>
        {
            try
            {
                var factory = _services.GetRequiredService<IDbContextFactory<CloudPanDbContext>>();
                await using var db = await factory.CreateDbContextAsync();
                string expiry = DateTime.UtcNow.AddHours(-24).ToString("O");
                var stale = await db.ChunkedUploads
                    .Where(c => string.Compare(c.CreatedAt, expiry) < 0)
                    .ToListAsync();
                foreach (var s in stale)
                {
                    try { if (File.Exists(s.TempPath)) { File.Delete(s.TempPath); } } catch (Exception ex) { _logger.LogWarning(ex, "删除超时分块临时文件失败: {TempPath}", s.TempPath); }
                    db.ChunkedUploads.Remove(s);
                }

                if (stale.Count > 0)
                {
                    await db.SaveChangesAsync();
                    _logger.LogInformation("清理超时分块上传: {Count} 条", stale.Count);
                }
            }
            catch (Exception ex) { _logger.LogWarning(ex, "分块上传定时清理异常"); }
        });
    }

    // ==================== WAL checkpoint（防 WAL 无限增长） ====================

    private void WalCheckpoint(object? state)
    {
        Task.Run(async () =>
        {
            try
            {
                var factory = _services.GetRequiredService<IDbContextFactory<CloudPanDbContext>>();
                await using var db = await factory.CreateDbContextAsync();
                await db.Database.ExecuteSqlRawAsync("PRAGMA wal_checkpoint(TRUNCATE);");
            }
            catch (Exception ex) { _logger.LogWarning(ex, "WAL checkpoint 异常"); }
        });
    }

    // ==================== 内存监控（超 500MB 告警） ====================

    private void MemoryMonitor(object? state)
    {
        try
        {
            long ws = Environment.WorkingSet / 1_048_576L;
            if (ws > 500)
            {
                _logger.LogWarning("内存使用偏高: WorkingSet={WsMem}MB", ws);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "内存监控定时检查异常");
        }
    }

    // ==================== 统一存储回收（T-088：.versions 孤儿存档 + .thumbnails 过期缓存） ====================

    private void StorageReclaim(object? state)
    {
        Task.Run(async () =>
        {
            try
            {
                var index = _services.GetRequiredService<IFileIndexService>();
                int orphans = await index.PurgeOrphanVersionArchivesAsync();
                if (orphans > 0)
                {
                    _logger.LogInformation("清理孤儿版本存档: {Count} 个", orphans);
                }
            }
            catch (Exception ex) { _logger.LogWarning(ex, "孤儿版本存档清理异常"); }

            try
            {
                var thumbnails = _services.GetRequiredService<IThumbnailService>();
                int reclaimed = await thumbnails.ReclaimExpiredThumbnailsAsync(DateTime.UtcNow - ThumbnailCacheRetention);
                if (reclaimed > 0)
                {
                    _logger.LogInformation("清理过期缩略图缓存: {Count} 个", reclaimed);
                }
            }
            catch (Exception ex) { _logger.LogWarning(ex, "缩略图缓存清理异常"); }
        });
    }
}
