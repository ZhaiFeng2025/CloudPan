using CloudPan.Infrastructure.Models;

namespace CloudPan.Infrastructure.Persistence.Client;

/// <summary>
/// 客户端本地持久化存储抽象（T-093）：封装 ClientDbContext 的查询与提交，
/// 客户端领域层只依赖本接口（不接触 EF Core 类型），持久化边界封闭在 Infrastructure。
/// 每次操作经 <see cref="IClientStoreFactory.CreateStoreAsync"/> 创建实例，用后 Dispose；
/// 返回的实体由底层 DbContext 追踪，属性修改在 <see cref="CommitAsync"/> 时持久化。
/// </summary>
public interface IClientStore : IAsyncDisposable
{
    // ======================== SyncQueue ========================

    /// <summary>按路径查询队列项；<paramref name="operations"/> 为空则匹配该路径全部操作。</summary>
    Task<List<SyncQueue>> GetQueuesByPathAsync(string filePath, IReadOnlyList<int>? operations, CancellationToken ct = default);

    /// <summary>按路径 + 操作查单一队列项（无则 null）。</summary>
    Task<SyncQueue?> GetQueueByPathAndOperationAsync(string filePath, int operation, CancellationToken ct = default);

    /// <summary>查询指定操作的全部队列项（如重命名待决窗口）。</summary>
    Task<List<SyncQueue>> GetQueuesByOperationAsync(int operation, CancellationToken ct = default);

    /// <summary>按路径前缀查询队列项（含路径自身；<paramref name="excludeId"/> 非空时排除指定项）。</summary>
    Task<List<SyncQueue>> GetQueuesByPrefixAsync(string path, int? excludeId, CancellationToken ct = default);

    /// <summary>查询全部队列项（内存过滤场景，如排除集热更新）。</summary>
    Task<List<SyncQueue>> GetAllQueuesAsync(CancellationToken ct = default);

    /// <summary>查询待处理传输项（Upload/Download/Delete，供浏览/状态视图叠加瞬态状态）。</summary>
    Task<List<SyncQueue>> GetPendingTransferQueuesAsync(CancellationToken ct = default);

    /// <summary>查询下一批待处理队列项：优先级降序 → 入队时间升序，取前 <paramref name="take"/> 条；冲突路径从候选中剔除。</summary>
    Task<List<SyncQueue>> GetNextQueueBatchAsync(IReadOnlyCollection<string>? excludedPaths, int take, CancellationToken ct = default);

    /// <summary>是否存在同路径未决下载项（下载窗口保护）。</summary>
    Task<bool> HasPendingDownloadAsync(string filePath, CancellationToken ct = default);

    /// <summary>队列总数与总字节数（进度重算）。</summary>
    Task<(int Count, long TotalBytes)> GetQueueTotalsAsync(CancellationToken ct = default);

    /// <summary>队列剩余条数。</summary>
    Task<int> GetQueueCountAsync(CancellationToken ct = default);

    /// <summary>登记新队列项（待 SaveChanges 提交）。</summary>
    void AddQueueItem(SyncQueue item);

    /// <summary>移除单个队列项。</summary>
    void RemoveQueueItem(SyncQueue item);

    /// <summary>批量移除队列项。</summary>
    void RemoveQueueItems(IEnumerable<SyncQueue> items);

    // ======================== RemoteSnapshot ========================

    /// <summary>按路径查询远程快照（无则 null）。</summary>
    Task<RemoteSnapshot?> GetSnapshotAsync(string path, CancellationToken ct = default);

    /// <summary>查询全部远程快照（浏览/状态视图全量加载）。</summary>
    Task<List<RemoteSnapshot>> GetAllSnapshotsAsync(CancellationToken ct = default);

    /// <summary>查询目录快照路径（Type==Directory），供选择性同步目录树。</summary>
    Task<List<string>> GetDirectoryPathsAsync(CancellationToken ct = default);

    /// <summary>查询路径前缀下的快照（含路径自身），供目录重命名前缀跟随。</summary>
    Task<List<RemoteSnapshot>> GetSnapshotsByPrefixAsync(string path, CancellationToken ct = default);

    /// <summary>分批查询快照（按路径排序，全量扫描分页）。</summary>
    Task<List<RemoteSnapshot>> GetSnapshotsPagedAsync(int skip, int take, CancellationToken ct = default);

    /// <summary>登记新快照。</summary>
    void AddSnapshot(RemoteSnapshot snapshot);

    /// <summary>移除单个快照。</summary>
    void RemoveSnapshot(RemoteSnapshot snapshot);

    /// <summary>批量移除快照。</summary>
    void RemoveSnapshots(IEnumerable<RemoteSnapshot> snapshots);

    // ======================== SyncCursor ========================

    /// <summary>查询同步游标（单行 Id=1，无则 null）。</summary>
    Task<SyncCursor?> GetCursorAsync(CancellationToken ct = default);

    /// <summary>登记新游标。</summary>
    void AddCursor(SyncCursor cursor);

    // ======================== 提交 ========================

    /// <summary>提交本次变更（对已追踪实体生效）。</summary>
    Task<int> CommitAsync(CancellationToken ct = default);
}

/// <summary>客户端持久化存储工厂抽象：领域层经此创建 <see cref="IClientStore"/> 实例。</summary>
public interface IClientStoreFactory
{
    /// <summary>创建存储实例（每次调用新建连接，用后由调用方 Dispose）。</summary>
    Task<IClientStore> CreateStoreAsync(CancellationToken ct = default);
}
