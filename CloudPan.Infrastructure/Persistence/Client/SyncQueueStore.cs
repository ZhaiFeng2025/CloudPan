using CloudPan.Contract;
using CloudPan.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;

namespace CloudPan.Infrastructure.Persistence.Client;

/// <summary>
/// IClientStore 的 EF Core 实现（T-093）：内部持有 ClientDbContext，所有查询/提交在此封闭。
/// 领域层（CloudPan.Client.Core）不再直接触碰 EF LINQ，替换存储/InMemory 单测成为可能。
/// </summary>
public sealed class SyncQueueStore : IClientStore
{
    private readonly ClientDbContext _db;

    /// <summary>以既有 DbContext 构造（由 <see cref="ClientStoreFactory"/> 创建）。</summary>
    public SyncQueueStore(ClientDbContext db) => _db = db;

    // ======================== SyncQueue ========================

    /// <inheritdoc/>
    public Task<List<SyncQueue>> GetQueuesByPathAsync(string filePath, IReadOnlyList<int>? operations, CancellationToken ct = default)
    {
        IQueryable<SyncQueue> query = _db.SyncQueue.Where(q => q.FilePath == filePath);
        if (operations is { Count: > 0 })
        {
            query = query.Where(q => operations.Contains(q.Operation));
        }
        return query.ToListAsync(ct);
    }

    /// <inheritdoc/>
    public Task<SyncQueue?> GetQueueByPathAndOperationAsync(string filePath, int operation, CancellationToken ct = default)
        => _db.SyncQueue.FirstOrDefaultAsync(q => q.FilePath == filePath && q.Operation == operation, ct);

    /// <inheritdoc/>
    public Task<List<SyncQueue>> GetQueuesByOperationAsync(int operation, CancellationToken ct = default)
        => _db.SyncQueue.Where(q => q.Operation == operation).ToListAsync(ct);

    /// <inheritdoc/>
    public async Task<List<SyncQueue>> GetQueuesByPrefixAsync(string path, int? excludeId, CancellationToken ct = default)
    {
        string key = path.TrimEnd('/');
        string dir = key + "/";
        IQueryable<SyncQueue> query = _db.SyncQueue;
        if (excludeId.HasValue)
        {
            query = query.Where(q => q.Id != excludeId.Value);
        }
        return await query
            .Where(q => q.FilePath == key || q.FilePath.StartsWith(dir))
            .ToListAsync(ct);
    }

    /// <inheritdoc/>
    public Task<List<SyncQueue>> GetAllQueuesAsync(CancellationToken ct = default)
        => _db.SyncQueue.ToListAsync(ct);

    /// <inheritdoc/>
    public Task<List<SyncQueue>> GetPendingTransferQueuesAsync(CancellationToken ct = default)
        => _db.SyncQueue
            .Where(q => q.Operation == (int)SyncOperation.Upload
                     || q.Operation == (int)SyncOperation.Download
                     || q.Operation == (int)SyncOperation.Delete)
            .ToListAsync(ct);

    /// <inheritdoc/>
    public async Task<List<SyncQueue>> GetNextQueueBatchAsync(IReadOnlyCollection<string>? excludedPaths, int take, CancellationToken ct = default)
    {
        IQueryable<SyncQueue> query = _db.SyncQueue;
        if (excludedPaths is { Count: > 0 })
        {
            query = query.Where(q => !excludedPaths.Contains(q.FilePath));
        }
        return await query
            .OrderByDescending(q => q.Priority)
            .ThenBy(q => q.CreatedAt)
            .Take(take)
            .ToListAsync(ct);
    }

    /// <inheritdoc/>
    public Task<bool> HasPendingDownloadAsync(string filePath, CancellationToken ct = default)
        => _db.SyncQueue.AnyAsync(q => q.FilePath == filePath && q.Operation == (int)SyncOperation.Download, ct);

    /// <inheritdoc/>
    public async Task<(int Count, long TotalBytes)> GetQueueTotalsAsync(CancellationToken ct = default)
    {
        int count = await _db.SyncQueue.CountAsync(ct);
        long total = await _db.SyncQueue.SumAsync(q => q.FileSize ?? 0, ct);
        return (count, total);
    }

    /// <inheritdoc/>
    public Task<int> GetQueueCountAsync(CancellationToken ct = default)
        => _db.SyncQueue.CountAsync(ct);

    /// <inheritdoc/>
    public void AddQueueItem(SyncQueue item) => _db.SyncQueue.Add(item);

    /// <inheritdoc/>
    public void RemoveQueueItem(SyncQueue item) => _db.SyncQueue.Remove(item);

    /// <inheritdoc/>
    public void RemoveQueueItems(IEnumerable<SyncQueue> items) => _db.SyncQueue.RemoveRange(items);

    // ======================== RemoteSnapshot ========================

    /// <inheritdoc/>
    public Task<RemoteSnapshot?> GetSnapshotAsync(string path, CancellationToken ct = default)
        => _db.RemoteSnapshots.FindAsync(new object?[] { path }, ct).AsTask();

    /// <inheritdoc/>
    public Task<List<RemoteSnapshot>> GetAllSnapshotsAsync(CancellationToken ct = default)
        => _db.RemoteSnapshots.ToListAsync(ct);

    /// <inheritdoc/>
    public Task<List<string>> GetDirectoryPathsAsync(CancellationToken ct = default)
        => _db.RemoteSnapshots
            .Where(s => s.Type == (int)FileType.Directory)
            .Select(s => s.Path)
            .ToListAsync(ct);

    /// <inheritdoc/>
    public async Task<List<RemoteSnapshot>> GetSnapshotsByPrefixAsync(string path, CancellationToken ct = default)
    {
        string key = path.TrimEnd('/');
        string dir = key + "/";
        return await _db.RemoteSnapshots
            .Where(s => s.Path == key || s.Path.StartsWith(dir))
            .ToListAsync(ct);
    }

    /// <inheritdoc/>
    public Task<List<RemoteSnapshot>> GetSnapshotsPagedAsync(int skip, int take, CancellationToken ct = default)
        => _db.RemoteSnapshots
            .OrderBy(s => s.Path)
            .Skip(skip)
            .Take(take)
            .ToListAsync(ct);

    /// <inheritdoc/>
    public void AddSnapshot(RemoteSnapshot snapshot) => _db.RemoteSnapshots.Add(snapshot);

    /// <inheritdoc/>
    public void RemoveSnapshot(RemoteSnapshot snapshot) => _db.RemoteSnapshots.Remove(snapshot);

    /// <inheritdoc/>
    public void RemoveSnapshots(IEnumerable<RemoteSnapshot> snapshots) => _db.RemoteSnapshots.RemoveRange(snapshots);

    // ======================== SyncCursor ========================

    /// <inheritdoc/>
    public Task<SyncCursor?> GetCursorAsync(CancellationToken ct = default)
        => _db.SyncCursor.FindAsync(new object?[] { 1 }, ct).AsTask();

    /// <inheritdoc/>
    public void AddCursor(SyncCursor cursor) => _db.SyncCursor.Add(cursor);

    // ======================== 提交 ========================

    /// <inheritdoc/>
    public Task<int> CommitAsync(CancellationToken ct = default) => _db.SaveChangesAsync(ct);

    /// <summary>释放底层 DbContext。</summary>
    public ValueTask DisposeAsync() => _db.DisposeAsync();
}

/// <summary>
/// IClientStoreFactory 的 EF Core 实现（T-093）：委托底层 IDbContextFactory&lt;ClientDbContext&gt; 创建连接。
/// </summary>
public sealed class ClientStoreFactory : IClientStoreFactory
{
    private readonly IDbContextFactory<ClientDbContext> _dbFactory;

    /// <summary>以既有 DbContext 工厂构造。</summary>
    public ClientStoreFactory(IDbContextFactory<ClientDbContext> dbFactory) => _dbFactory = dbFactory;

    /// <inheritdoc/>
    public async Task<IClientStore> CreateStoreAsync(CancellationToken ct = default)
        => new SyncQueueStore(await _dbFactory.CreateDbContextAsync(ct));
}
