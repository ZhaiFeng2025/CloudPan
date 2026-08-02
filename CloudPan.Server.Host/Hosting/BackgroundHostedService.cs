using CloudPan.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
// Host 是 WinForms 项目，隐式 using 含 System.Windows.Forms.Timer，用别名消除歧义
using Timer = System.Threading.Timer;

namespace CloudPan.Server.Hosting;

/// <summary>
/// 后台定时任务宿主：回收站 30 天清理、超时分块清理、WAL checkpoint、内存监控。
/// 替换 Program.cs 中的裸 Timer（R-A6：定时任务用 IHostedService）。Timer 回调使用 Task.Run 包裹并捕获全部异常（CLAUDE.md 7.2）。
/// </summary>
public sealed class BackgroundHostedService : IHostedService, IDisposable
{
    private readonly string _syncRoot;
    private readonly IServiceProvider _services;
    private readonly ILogger<BackgroundHostedService> _logger;
    private Timer? _trashTimer;
    private Timer? _chunkTimer;
    private Timer? _walTimer;
    private Timer? _memTimer;

    public BackgroundHostedService(string syncRoot, IServiceProvider services, ILogger<BackgroundHostedService> logger)
    {
        _syncRoot = syncRoot;
        _services = services;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _trashTimer = new Timer(TrashCleanup, null, TimeSpan.FromMinutes(10), TimeSpan.FromHours(6));
        _chunkTimer = new Timer(ChunkCleanup, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30));
        _walTimer = new Timer(WalCheckpoint, null, TimeSpan.FromMinutes(60), TimeSpan.FromMinutes(60));
        _memTimer = new Timer(MemoryMonitor, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(10));
        _logger.LogInformation("后台定时任务已启动（回收站/分块/WAL/内存监控）");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _trashTimer?.Dispose();
        _chunkTimer?.Dispose();
        _walTimer?.Dispose();
        _memTimer?.Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _trashTimer?.Dispose();
        _chunkTimer?.Dispose();
        _walTimer?.Dispose();
        _memTimer?.Dispose();
    }

    // ==================== 回收站 30 天自动清理 ====================

    private void TrashCleanup(object? state)
    {
        try
        {
            string trashDir = Path.Combine(_syncRoot, ".cloudpan", ".trash");
            if (!Directory.Exists(trashDir))
            {
                return;
            }

            DateTime cutoff = DateTime.UtcNow.AddDays(-30);
            foreach (string metaFile in Directory.GetFiles(trashDir, "*.json"))
            {
                try
                {
                    string json = File.ReadAllText(metaFile);
                    using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(json);
                    var root = doc.RootElement;
                    string? deletedAt = root.TryGetProperty("DeletedAt", out var da) ? da.GetString() : null;
                    string? trashFileName = root.TryGetProperty("TrashFileName", out var tn) ? tn.GetString() : null;
                    if (deletedAt != null && trashFileName != null
                        && DateTime.TryParse(deletedAt, out DateTime delTime) && delTime < cutoff)
                    {
                        string trashFile = Path.Combine(trashDir, trashFileName);
                        if (File.Exists(trashFile))
                        {
                            File.Delete(trashFile);
                        }

                        if (Directory.Exists(trashFile))
                        {
                            Directory.Delete(trashFile, recursive: true);
                        }

                        File.Delete(metaFile);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "清理回收站文件异常: {MetaFile}", metaFile);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "回收站定时清理异常");
        }
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
}
