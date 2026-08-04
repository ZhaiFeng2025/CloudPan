using CloudPan.Infrastructure.Models;
using CloudPan.Infrastructure.Persistence.Client;

namespace CloudPan.Client.Core.Services;

/// <summary>
/// SyncBrowseService 部分实现（T-108 拆分）：浏览数据缓存。
/// /api/tree 快照（RemoteSnapshots 表）内存缓存 + 本地文件索引（BrowseLocalIndex），
/// 消除 UI 刷新时的全树递归枚举与快照全表读取。查询方法见 SyncBrowseService.cs。
/// </summary>
internal sealed partial class SyncBrowseService
{
    // T-108：本地文件/目录缓存（FileSystemWatcher 增量维护，消除全树递归枚举）
    private readonly BrowseLocalIndex _localIndex;

    // T-108：/api/tree 快照内存缓存（RefreshSnapshotCacheAsync 后台刷新，消除 UI 线程全表读取）
    private readonly object _snapLock = new();
    private IReadOnlyList<RemoteSnapshot>? _snapshots;
    private long _snapGen;
    private long _dataVersion; // 组合数据版本：本地索引变更 + 快照代际变化（Interlocked 读写）

    /// <summary>本地索引数据变更回调（FSW 事件线程）：递增组合数据版本，供 UI 判断是否需重渲染。</summary>
    private void OnLocalIndexChanged() => Interlocked.Increment(ref _dataVersion);

    public void Dispose()
    {
        _localIndex.Changed -= OnLocalIndexChanged;
        _localIndex.Dispose();
    }

    /// <summary>
    /// 后台刷新 /api/tree 快照缓存（RemoteSnapshots 全表读取移出 UI 线程）。
    /// 返回当前组合数据版本；UI 定时器据此判断当前浏览数据是否变化、决定是否重渲染。
    /// </summary>
    public Task<long> RefreshSnapshotCacheAsync(CancellationToken ct = default)
        // T-108：Task.Run 确保全表读取从头在后台线程执行（CreateDbContextAsync 默认同步完成）。
        => Task.Run(() => RefreshSnapshotCacheCoreAsync(ct), ct);

    private async Task<long> RefreshSnapshotCacheCoreAsync(CancellationToken ct)
    {
        await using var store = await _storeFactory.CreateStoreAsync(ct).ConfigureAwait(false);
        var fresh = await store.GetAllSnapshotsAsync(ct).ConfigureAwait(false);

        lock (_snapLock)
        {
            if (!SnapshotsEqual(fresh, _snapshots))
            {
                _snapshots = fresh;
                _snapGen++;
                Interlocked.Increment(ref _dataVersion);
            }

            return Interlocked.Read(ref _dataVersion);
        }
    }

    /// <summary>读取快照缓存；首次/失效时后台刷新后返回（查询侧不重复全表读取）。</summary>
    private async Task<IReadOnlyList<RemoteSnapshot>> GetSnapshotListAsync(CancellationToken ct)
    {
        lock (_snapLock)
        {
            if (_snapshots != null)
            {
                return _snapshots;
            }
        }

        await RefreshSnapshotCacheAsync(ct).ConfigureAwait(false);
        lock (_snapLock)
        {
            return _snapshots ?? Array.Empty<RemoteSnapshot>();
        }
    }

    /// <summary>比较两份快照列表是否相等（仅比对浏览视图相关字段；不比对 Hash/LastModified/IsDownloaded）。</summary>
    private static bool SnapshotsEqual(IReadOnlyList<RemoteSnapshot> fresh, IReadOnlyList<RemoteSnapshot>? cached)
    {
        if (cached == null || fresh.Count != cached.Count)
        {
            return false;
        }

        var cachedByPath = new Dictionary<string, RemoteSnapshot>(cached.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var c in cached)
        {
            cachedByPath[c.Path] = c;
        }

        foreach (var f in fresh)
        {
            if (!cachedByPath.TryGetValue(f.Path, out var c)
                || f.Type != c.Type || f.State != c.State
                || f.Version != c.Version || f.Size != c.Size)
            {
                return false;
            }
        }

        return true;
    }
}
