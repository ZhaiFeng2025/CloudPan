using System.Security.Cryptography;
using CloudPan.Shared;
using CloudPan.Client.Models;
using Microsoft.EntityFrameworkCore;

namespace CloudPan.Client.Services;

/// <summary>
/// 同步引擎——客户端状态机核心。
/// 空闲 → 扫描变更 → 比对哈希 → 传输 → 应用变更 → 空闲
/// </summary>
public class SyncEngine
{
    private readonly ApiClient _api;
    private readonly string _syncRoot;
    private readonly IDbContextFactory<ClientDbContext> _dbFactory;
    private readonly ILogger _logger;

    private volatile bool _running;
    private volatile bool _paused;

    public event Action<string>? StatusChanged;
    public event Action<int, int>? QueueProgressChanged; // (completed, total)

    public SyncEngine(ApiClient api, string syncRoot, IDbContextFactory<ClientDbContext> dbFactory, ILogger logger)
    {
        _api = api;
        _syncRoot = syncRoot;
        _dbFactory = dbFactory;
        _logger = logger;
    }

    /// <summary>启动同步引擎。</summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        _running = true;
        _logger.Info("同步引擎启动");

        // 首次启动：拉取全量文件树
        await FullSyncAsync(ct);

        // 主循环
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
                    _logger.Error($"同步周期异常: {ex.Message}");
                }
            }
            await Task.Delay(3000, ct); // 3 秒轮询间隔
        }
    }

    /// <summary>暂停/恢复。</summary>
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
        var existing = await db.SyncQueue
            .FirstOrDefaultAsync(q => q.FilePath == relativePath && q.Operation == (int)operation);

        if (existing == null)
        {
            var fileSize = 0;
            var fullPath = Path.Combine(_syncRoot, relativePath.TrimStart('/'));
            if (File.Exists(fullPath)) fileSize = (int)new FileInfo(fullPath).Length;

            db.SyncQueue.Add(new SyncQueueItem
            {
                FilePath = relativePath,
                Operation = (int)operation,
                Priority = fileSize < 1_048_576 ? (int)QueuePriority.High : (int)QueuePriority.Normal,
                FileSize = fileSize
            });
            await db.SaveChangesAsync();
            _logger.Info($"入队: {operation} {relativePath}");
        }
    }

    // ============================================================
    // 私有方法
    // ============================================================

    private async Task FullSyncAsync(CancellationToken ct)
    {
        NotifyStatus("首次全量同步...");
        _logger.Info("开始全量同步");

        await using var db = await _dbFactory.CreateDbContextAsync();
        var cursor = await db.SyncCursor.FindAsync(1);

        var response = await _api.GetFileTreeAsync(cursor?.LastMaxVersion ?? 0);
        if (response == null) return;

        foreach (var item in response.Data)
        {
            var localPath = ToLocalPath(item.Path);
            var snapshot = await db.RemoteSnapshots.FindAsync(item.Path);

            if (snapshot == null || snapshot.Version < item.Version)
            {
                // 需要下载
                if (item.Path.Contains('/')) // is file (simplified: check for extension later)
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

            // 更新快照
            if (snapshot == null)
            {
                db.RemoteSnapshots.Add(new RemoteSnapshot
                {
                    Path = item.Path,
                    Type = item.Type,
                    Hash = item.CurrentHash,
                    Size = item.CurrentSize,
                    Version = item.Version,
                    State = item.State
                });
            }
            else
            {
                snapshot.Hash = item.CurrentHash;
                snapshot.Size = item.CurrentSize;
                snapshot.Version = item.Version;
                snapshot.State = item.State;
            }
        }

        // 更新游标
        if (cursor == null)
        {
            db.SyncCursor.Add(new SyncCursorState { Id = 1, LastMaxVersion = response.MaxVersion, LastSyncAt = DateTime.UtcNow.ToString("O") });
        }
        else
        {
            cursor.LastMaxVersion = response.MaxVersion;
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

        var response = await _api.GetFileTreeAsync(sinceVersion);
        if (response == null || response.Data.Count == 0) return;

        foreach (var item in response.Data)
        {
            var localPath = ToLocalPath(item.Path);
            var snapshot = await db.RemoteSnapshots.FindAsync(item.Path);

            if (item.State == (int)FileState.Deleting)
            {
                // 远程已删除 → 删除本地文件
                if (File.Exists(localPath)) File.Delete(localPath);
                db.RemoteSnapshots.Remove(snapshot!);
                _logger.Info($"同步删除: {item.Path}");
            }
            else if (snapshot == null || snapshot.Version < item.Version)
            {
                // 新增或更新 → 入队下载
                if (item.Type == (int)FileType.File)
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

            // 更新快照
            if (snapshot != null)
            {
                snapshot.Version = item.Version;
                snapshot.Hash = item.CurrentHash;
                snapshot.Size = item.CurrentSize;
                snapshot.State = item.State;
            }
            else if (item.Type == (int)FileType.File)
            {
                db.RemoteSnapshots.Add(new RemoteSnapshot
                {
                    Path = item.Path,
                    Type = item.Type,
                    Hash = item.CurrentHash,
                    Size = item.CurrentSize,
                    Version = item.Version,
                    State = item.State
                });
            }
        }

        if (cursor != null && response.MaxVersion > cursor.LastMaxVersion)
        {
            cursor.LastMaxVersion = response.MaxVersion;
            cursor.LastSyncAt = DateTime.UtcNow.ToString("O");
        }

        await db.SaveChangesAsync();
    }

    private async Task ProcessQueueAsync(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var items = await db.SyncQueue
            .OrderByDescending(q => q.Priority)
            .ThenBy(q => q.CreatedAt)
            .Take(5)
            .ToListAsync();

        if (items.Count == 0) return;

        var total = await db.SyncQueue.CountAsync();
        QueueProgressChanged?.Invoke(0, total);

        foreach (var item in items)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                if (item.Operation == (int)SyncOperation.Upload)
                {
                    await ProcessUploadAsync(item, ct);
                }
                else if (item.Operation == (int)SyncOperation.Download)
                {
                    await ProcessDownloadAsync(item, ct);
                }
                else if (item.Operation == (int)SyncOperation.Delete)
                {
                    await ProcessDeleteAsync(item, ct);
                }

                db.SyncQueue.Remove(item);
                await db.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                item.RetryCount++;
                item.LastError = ex.Message;
                if (item.RetryCount >= 10)
                {
                    _logger.Error($"传输失败（已达最大重试）: {item.FilePath} — {ex.Message}");
                    db.SyncQueue.Remove(item);
                }
                await db.SaveChangesAsync();
            }
        }
    }

    private async Task ProcessUploadAsync(SyncQueueItem item, CancellationToken ct)
    {
        var localPath = ToLocalPath(item.FilePath);
        if (!File.Exists(localPath))
        {
            _logger.Warn($"上传跳过——文件不存在: {localPath}");
            return;
        }

        var lastModified = File.GetLastWriteTimeUtc(localPath).ToString("O");
        NotifyStatus($"上传: {item.FilePath}");

        var result = await _api.UploadAsync(localPath, item.FilePath, item.BaseVersion ?? 0, lastModified);
        _logger.Info($"上传完成: {item.FilePath} → v{result?.Data.Version}");
    }

    private async Task ProcessDownloadAsync(SyncQueueItem item, CancellationToken ct)
    {
        var localPath = ToLocalPath(item.FilePath);
        NotifyStatus($"下载: {item.FilePath}");

        await _api.DownloadAsync(item.FilePath, localPath);

        // 设置文件时间为服务端时间（避免触发二次上传）
        if (File.Exists(localPath))
            File.SetLastWriteTimeUtc(localPath, DateTime.UtcNow);

        _logger.Info($"下载完成: {item.FilePath}");
    }

    private async Task ProcessDeleteAsync(SyncQueueItem item, CancellationToken ct)
    {
        var localPath = ToLocalPath(item.FilePath);
        if (File.Exists(localPath))
        {
            File.Delete(localPath);
            _logger.Info($"本地删除: {item.FilePath}");
        }

        try
        {
            await _api.DeleteAsync(item.FilePath, item.BaseVersion ?? 0);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // 服务端已删除，忽略
        }
    }

    private string ToLocalPath(string relativePath)
    {
        return Path.Combine(_syncRoot, relativePath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
    }

    private void NotifyStatus(string status)
    {
        StatusChanged?.Invoke(status);
    }
}

/// <summary>简易日志接口。</summary>
public interface ILogger
{
    void Info(string msg);
    void Warn(string msg);
    void Error(string msg);
}

public class ConsoleLogger : ILogger
{
    public void Info(string msg) => Console.WriteLine($"[INFO] {DateTime.Now:HH:mm:ss} {msg}");
    public void Warn(string msg) => Console.WriteLine($"[WARN] {DateTime.Now:HH:mm:ss} {msg}");
    public void Error(string msg) => Console.WriteLine($"[ERROR] {DateTime.Now:HH:mm:ss} {msg}");
}
