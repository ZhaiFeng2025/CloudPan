using System.Text.RegularExpressions;
using CloudPan.Contract;
using CloudPan.Infrastructure.Models;
using CloudPan.Infrastructure.Persistence.Client;
using Microsoft.Extensions.Logging;

namespace CloudPan.Client.Core.Services;

/// <summary>文件浏览项——供文件浏览视图（列表/网格）渲染，数据源为 /api/tree 快照（RemoteSnapshots 本地缓存）+ 本地文件系统。</summary>
public sealed record FileBrowseItem(
    string Path,
    string Name,
    bool IsDirectory,
    long Size,
    int Version,
    int State,
    bool LocalExists);

/// <summary>每文件同步状态视图项——供 UI 渲染每文件状态图标（✓↻!✗☁）。</summary>
public sealed record FileSyncStatusItem(string RelativePath, bool IsDirectory, int State, bool LocalExists);

/// <summary>
/// 同步引擎查询/读取服务（T-070 拆分）：文件浏览、每文件状态、冲突对比下载。
/// 只读操作，不触碰同步状态机的可变状态（计数器/事件/锁/排除集热更新），
/// 依赖注入 ApiClient/DbContextFactory，路径逻辑统一走 <see cref="SyncPath"/>。
///
/// T-108 性能改造：浏览数据改为「/api/tree 快照（RemoteSnapshots 内存缓存）+ FileSystemWatcher
/// 增量维护的本地文件索引」，UI 刷新不再对同步根做全树递归枚举 + 快照全表读取；全流程
/// ConfigureAwait(false) 使枚举/DB 读取全部在后台线程执行，UI 线程仅 await。
/// 快照缓存与本地索引字段见 SyncBrowseService.Cache.cs。
/// </summary>
internal sealed partial class SyncBrowseService : IDisposable
{
    private readonly IApiClient _api;
    private readonly IClientStoreFactory _storeFactory;
    private readonly ILogger<SyncEngine> _logger;
    private readonly string _syncRoot;
    private readonly List<Regex> _ignorePatterns;

    public SyncBrowseService(
        IApiClient api,
        IClientStoreFactory storeFactory,
        ILogger<SyncEngine> logger,
        string syncRoot,
        List<Regex> ignorePatterns)
    {
        _api = api;
        _storeFactory = storeFactory;
        _logger = logger;
        _syncRoot = syncRoot;
        _ignorePatterns = ignorePatterns;
        _localIndex = new BrowseLocalIndex(syncRoot, logger, ignorePatterns);
        _localIndex.Changed += OnLocalIndexChanged;
    }

    /// <summary>
    /// 返回浏览视图数据：目录模式下返回 <paramref name="directoryPath"/> 的直接子项；
    /// 搜索模式下返回其下所有路径中名称包含关键字的项（含深层子目录，递归定位文件）。
    /// 快照（RemoteSnapshots，即 /api/tree 拉取结果缓存）覆盖服务端文件（含 CloudOnly），
    /// 本地有而快照无的项并入（Modified/Uploading 瞬态）。
    /// 墓碑（Deleting）项不展示（删除中的文件从浏览视图消失）。
    /// </summary>
    public Task<IReadOnlyList<FileBrowseItem>> GetFileBrowserAsync(
        string directoryPath, string? searchText = null, CancellationToken ct = default)
        // T-108：Task.Run 确保查询（含本地索引首次全树构建）从头在后台线程执行（CreateDbContextAsync 默认同步完成）。
        => Task.Run(() => GetFileBrowserCoreAsync(directoryPath, searchText, ct), ct);

    private async Task<IReadOnlyList<FileBrowseItem>> GetFileBrowserCoreAsync(
        string directoryPath, string? searchText, CancellationToken ct)
    {
        await using var store = await _storeFactory.CreateStoreAsync(ct).ConfigureAwait(false);

        // 本地索引首次构建（后台线程；此后由 FileSystemWatcher 增量维护，不再全树递归枚举）
        _localIndex.EnsureInitialized();

        // 1. 待处理队列 → 瞬态状态（Uploading/Downloading/Deleting），优先级高于快照状态
        var queueOps = await store.GetPendingTransferQueuesAsync(ct).ConfigureAwait(false);
        var queueStateByPath = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var q in queueOps)
        {
            int state = q.Operation switch
            {
                (int)SyncOperation.Upload => (int)FileState.Uploading,
                (int)SyncOperation.Download => (int)FileState.Downloading,
                _ => (int)FileState.Deleting
            };
            queueStateByPath[q.FilePath] = state;
        }

        // 2. 服务端快照（来自 /api/tree，本地 DB 缓存 → 内存缓存）
        var snapshots = await GetSnapshotListAsync(ct).ConfigureAwait(false);
        var snapshotByPath = new Dictionary<string, RemoteSnapshot>(StringComparer.OrdinalIgnoreCase);
        foreach (var snap in snapshots)
        {
            snapshotByPath[snap.Path] = snap;
        }

        // 3. 本地文件/目录集合（FileSystemWatcher 增量维护的缓存副本，无全树枚举）
        var (localFiles, localDirs) = _localIndex.CopySets();

        // 4. 归一化浏览路径："/" 根或 "/a/b" 形式（无尾斜杠）
        string normDir = SyncPath.NormalizePath(directoryPath) ?? "/";
        normDir = normDir.Replace('\\', '/').TrimEnd('/');
        if (normDir.Length == 0 || !normDir.StartsWith('/'))
        {
            normDir = "/" + normDir.TrimStart('/');
        }
        string dirPrefix = normDir == "/" ? "/" : normDir + "/";

        bool searching = !string.IsNullOrWhiteSpace(searchText);
        string needle = searching ? searchText!.Trim().ToLowerInvariant() : "";

        var items = new Dictionary<string, FileBrowseItem>(StringComparer.OrdinalIgnoreCase);

        void AddOrMerge(string rawPath, bool isDir, long size, int version, int state, bool localExists)
        {
            // 删除中的项（本地删除排队/服务端墓碑）从浏览视图消失
            if (state == (int)FileState.Deleting)
            {
                return;
            }

            // 路径归一化：去尾斜杠，避免目录路径 "/a/b/" 与 "/a/b" 重复
            string path = rawPath.TrimEnd('/');
            if (path.Length == 0)
            {
                return;
            }

            if (!path.StartsWith(dirPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            string rest = path[dirPrefix.Length..];
            if (rest.Length == 0)
            {
                return;
            }

            if (!searching)
            {
                // 目录模式：仅直接子项（rest 不含分隔符）
                if (rest.Contains('/'))
                {
                    return;
                }
            }
            else
            {
                string name = path[(path.LastIndexOf('/') + 1)..];
                if (!name.ToLowerInvariant().Contains(needle))
                {
                    return;
                }
            }

            if (items.ContainsKey(path))
            {
                return;
            }

            string displayName = path[(path.LastIndexOf('/') + 1)..];
            items[path] = new FileBrowseItem(path, displayName, isDir, size, version, state, localExists);
        }

        // 5. 快照项（含 CloudOnly；Deleting 墓碑由 AddOrMerge 跳过）
        foreach (var snap in snapshots)
        {
            bool isDir = snap.Type == (int)FileType.Directory;
            bool localExists = isDir ? localDirs.Contains(snap.Path) : localFiles.Contains(snap.Path);
            int state = queueStateByPath.TryGetValue(snap.Path, out int qState) ? qState : snap.State;
            AddOrMerge(snap.Path, isDir, snap.Size, snap.Version, state, localExists);
        }

        // 6. 本地新增项（快照无）——作为 Modified/Uploading 并入
        foreach (string rel in localFiles)
        {
            if (snapshotByPath.ContainsKey(rel))
            {
                continue;
            }

            // 提前过滤视图范围，避免对视图外文件做尺寸读取（全量本地仅存文件时的 O(N) 磁盘 I/O 瓶颈）
            if (!IsInBrowseView(rel, dirPrefix, searching, needle))
            {
                continue;
            }

            int state = queueStateByPath.TryGetValue(rel, out int qState) ? qState : (int)FileState.Modified;
            long size = 0;
            try
            {
                size = new FileInfo(SyncPath.ToLocalPath(_syncRoot, rel)).Length;
            }
            catch (FileNotFoundException)
            {
                // 本地已删除但 FSW 事件尚未处理 → 不作为本地新增项展示（T-108 缓存一致性）
                continue;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"读取本地文件大小失败 {rel}: {ex.Message}");
            }

            AddOrMerge(rel, false, size, 0, state, true);
        }

        foreach (string rel in localDirs)
        {
            if (snapshotByPath.ContainsKey(rel))
            {
                continue;
            }

            if (!IsInBrowseView(rel, dirPrefix, searching, needle))
            {
                continue;
            }

            // 本地目录已删除但 FSW 事件尚未处理 → 跳过（本地新增目录不展示）
            if (!Directory.Exists(SyncPath.ToLocalPath(_syncRoot, rel)))
            {
                continue;
            }

            int state = queueStateByPath.TryGetValue(rel, out int qState) ? qState : (int)FileState.Modified;
            AddOrMerge(rel, true, 0, 0, state, true);
        }

        var result = items.Values.ToList();
        // 默认排序：目录优先，同类型按名称（UI 可按需重排）
        result.Sort((a, b) =>
        {
            int byDir = (b.IsDirectory ? 1 : 0).CompareTo(a.IsDirectory ? 1 : 0);
            if (byDir != 0)
            {
                return byDir;
            }

            return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });
        return result;
    }

    /// <summary>判断相对路径是否落在当前浏览视图范围内（与 AddOrMerge 的前缀/名称过滤保持一致）。</summary>
    private static bool IsInBrowseView(string path, string dirPrefix, bool searching, string needle)
    {
        if (!path.StartsWith(dirPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string rest = path[dirPrefix.Length..];
        if (rest.Length == 0)
        {
            return false;
        }

        if (!searching)
        {
            return !rest.Contains('/');
        }

        string name = path[(path.LastIndexOf('/') + 1)..];
        return name.ToLowerInvariant().Contains(needle);
    }

    /// <summary>
    /// 读取同步目录每文件的当前同步状态：
    /// 服务端快照 FileState（Synced/CloudOnly/Deleting/Modified）+ 待处理队列（Uploading/Downloading/Deleting）+ 本地存在性。
    /// 冲突与错误由 UI 依据本地维护的冲突/错误列表叠加，不在此查询（避免与 UI 状态源重复）。
    /// </summary>
    public Task<IReadOnlyList<FileSyncStatusItem>> GetFileSyncStatusesAsync(CancellationToken ct = default)
        // T-108：Task.Run 确保查询（含本地索引首次全树构建）从头在后台线程执行。
        => Task.Run(() => GetFileSyncStatusesCoreAsync(ct), ct);

    private async Task<IReadOnlyList<FileSyncStatusItem>> GetFileSyncStatusesCoreAsync(CancellationToken ct)
    {
        await using var store = await _storeFactory.CreateStoreAsync(ct).ConfigureAwait(false);

        // 本地索引首次构建（后台线程；此后由 FileSystemWatcher 增量维护）
        _localIndex.EnsureInitialized();

        // 1. 待处理队列 → 瞬态状态（Uploading/Downloading/Deleting），优先级高于快照状态
        var queueOps = await store.GetPendingTransferQueuesAsync(ct).ConfigureAwait(false);
        var queueStateByPath = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var q in queueOps)
        {
            int state = q.Operation switch
            {
                (int)SyncOperation.Upload => (int)FileState.Uploading,
                (int)SyncOperation.Download => (int)FileState.Downloading,
                _ => (int)FileState.Deleting
            };
            queueStateByPath[q.FilePath] = state;
        }

        // 2. 服务端快照（Synced/CloudOnly/Deleting/Modified）
        var snapshots = await GetSnapshotListAsync(ct).ConfigureAwait(false);

        // 3. 本地文件/目录集合（相对路径，忽略 .cloudpan 与忽略规则，来自增量维护缓存）
        var (localFiles, localDirs) = _localIndex.CopySets();

        var results = new List<FileSyncStatusItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 4a. 快照项（含 CloudOnly 远端文件——本地无副本但用户需看到状态）
        foreach (var snap in snapshots)
        {
            seen.Add(snap.Path);
            bool isDir = snap.Type == (int)FileType.Directory;
            bool localExists = isDir ? localDirs.Contains(snap.Path) : localFiles.Contains(snap.Path);
            int state = queueStateByPath.TryGetValue(snap.Path, out int qState) ? qState : snap.State;
            results.Add(new FileSyncStatusItem(snap.Path, isDir, state, localExists));
        }

        // 4b. 本地有、快照无的文件/目录 → 新文件待上传（Modified/Uploading）
        foreach (string rel in localFiles)
        {
            if (seen.Contains(rel))
            {
                continue;
            }

            // 本地已删除但 FSW 事件尚未处理 → 跳过（缓存一致性）
            if (!File.Exists(SyncPath.ToLocalPath(_syncRoot, rel)))
            {
                continue;
            }

            seen.Add(rel);
            int state = queueStateByPath.TryGetValue(rel, out int qState) ? qState : (int)FileState.Modified;
            results.Add(new FileSyncStatusItem(rel, false, state, true));
        }

        foreach (string rel in localDirs)
        {
            if (seen.Contains(rel))
            {
                continue;
            }

            if (!Directory.Exists(SyncPath.ToLocalPath(_syncRoot, rel)))
            {
                continue;
            }

            seen.Add(rel);
            int state = queueStateByPath.TryGetValue(rel, out int qState) ? qState : (int)FileState.Modified;
            results.Add(new FileSyncStatusItem(rel, true, state, true));
        }

        // 按路径排序，便于逐文件定位
        results.Sort((a, b) => string.Compare(a.RelativePath, b.RelativePath, StringComparison.OrdinalIgnoreCase));
        return results;
    }

    /// <summary>
    /// 返回服务端目录树的全部目录路径（以 / 开头、以 / 结尾），供选择性同步面板填充勾选目录树。
    /// 数据源为 RemoteSnapshots（/api/tree 拉取结果的本地缓存），含空目录与 CloudOnly 目录。
    /// 返回空集合表示快照尚未加载（客户端未完成过同步拉取）或服务端确无目录。
    /// </summary>
    public async Task<List<string>> GetDirectoryTreePathsAsync(CancellationToken ct = default)
    {
        await using var store = await _storeFactory.CreateStoreAsync(ct).ConfigureAwait(false);
        var dirs = await store.GetDirectoryPathsAsync(ct).ConfigureAwait(false);

        // 规范化：目录路径统一 / 开头 + / 结尾（服务端条目路径以 / 开头、不含尾斜杠；排除集语义目录以 / 结尾）
        return dirs
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Replace('\\', '/'))
            .Select(p => p.StartsWith('/') ? p : "/" + p)
            .Select(p => p.EndsWith('/') ? p : p + "/")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// 下载服务端当前版本到临时目录，返回临时文件路径（用于冲突解决时的「打开两版本对比」）。
    /// 下载失败或服务端无此文件返回 null。
    /// </summary>
    public async Task<string?> DownloadRemoteToTempAsync(string relativePath, CancellationToken ct = default)
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "CloudPanCompare");
        Directory.CreateDirectory(tempDir);
        string ext = Path.GetExtension(relativePath);
        string tempPath = Path.Combine(tempDir,
            $"{Path.GetFileNameWithoutExtension(relativePath)}.remote{DateTime.Now:yyyyMMddHHmmss}{ext}");

        var result = await _api.DownloadAsync(relativePath, tempPath, ct: ct).ConfigureAwait(false);
        if (result == null || !File.Exists(tempPath))
        {
            return null;
        }
        _logger.LogInformation("已下载服务端版本到临时文件供对比: {Path} → {Temp}", relativePath, tempPath);
        return tempPath;
    }
}
