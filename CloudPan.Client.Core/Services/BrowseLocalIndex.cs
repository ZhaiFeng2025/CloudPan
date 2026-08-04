using System.Text.RegularExpressions;
using CloudPan.Infrastructure.Storage;
using Microsoft.Extensions.Logging;

namespace CloudPan.Client.Core.Services;

/// <summary>
/// 本地文件浏览缓存（T-108）：以 FileSystemWatcher 增量维护同步根内文件/目录相对路径集合，
/// 消除浏览刷新时对同步根的每 5 秒全树递归枚举。首次构建懒加载（须在后台线程调用），
/// 后续由文件系统事件（Created/Deleted/Renamed）增量更新；.syncignore 变更、FileSystemWatcher
/// 错误或每 ScanIntervalMinutes 分钟做一次后台全量重建兜底（对齐 FileWatcherService 全量扫描兜底）。
/// 忽略规则在写入时应用（查询侧不再做正则匹配），.syncignore 变更触发重建。
/// 线程安全：集合读写统一经 _gate 锁，集合引用可整体替换（重建），版本号经 Interlocked（CLAUDE.md 7.4）。
/// 文件系统事件/监控装配见 BrowseLocalIndex.Watchers.cs。
/// </summary>
internal sealed partial class BrowseLocalIndex : IDisposable
{
    private readonly string _syncRoot;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private HashSet<string> _files = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _dirs = new(StringComparer.OrdinalIgnoreCase);
    private volatile List<Regex> _ignorePatterns;
    private FileSystemWatcher? _watcher;
    private FileSystemWatcher? _ignoreWatcher;
    private System.Threading.Timer? _reconcileTimer;
    private long _version;
    private bool _initialized;
    private bool _disposed;

    /// <summary>本地数据变更事件：任一受跟踪文件/目录增删或重建后触发。调用方不得持有 _gate。</summary>
    public event Action? Changed;

    public BrowseLocalIndex(string syncRoot, ILogger logger, List<Regex> ignorePatterns)
    {
        _syncRoot = syncRoot;
        _logger = logger;
        _ignorePatterns = ignorePatterns;
    }

    /// <summary>当前数据版本（Interlocked 读取，浏览服务据此累计变更并判断是否需重渲染）。</summary>
    public long Version => Interlocked.Read(ref _version);

    /// <summary>首次查询时构建缓存并启动监控（须在后台线程调用；已构建则直接返回）。</summary>
    public void EnsureInitialized()
    {
        if (_initialized)
        {
            return;
        }

        lock (_gate)
        {
            if (_initialized)
            {
                return;
            }

            RebuildCore(notify: false);
            _initialized = true;
            StartReconcileTimer();
        }
    }

    /// <summary>线程安全快照：锁内复制当前文件/目录相对路径集合（副本可安全迭代）。</summary>
    public (HashSet<string> Files, HashSet<string> Dirs) CopySets()
    {
        lock (_gate)
        {
            return (new HashSet<string>(_files, StringComparer.OrdinalIgnoreCase),
                    new HashSet<string>(_dirs, StringComparer.OrdinalIgnoreCase));
        }
    }

    // ============================ 集合维护 ============================

    private bool AddEntry(string fullPath)
    {
        if (IsExcludedPath(fullPath) || SyncPath.ShouldIgnore(_syncRoot, fullPath, _ignorePatterns))
        {
            return false;
        }

        string rel = SyncPath.ToRelativePath(_syncRoot, fullPath);
        lock (_gate)
        {
            return Directory.Exists(fullPath) ? _dirs.Add(rel) : _files.Add(rel);
        }
    }

    private bool AddSubtree(string fullPath)
    {
        bool any = AddEntry(fullPath);
        try
        {
            // 递归补齐子树（整棵移入的目录不会逐子触发 Created）
            foreach (string child in Directory.EnumerateFileSystemEntries(fullPath, "*", SearchOption.AllDirectories))
            {
                any |= AddEntry(child);
            }
        }
        catch (Exception)
        {
            // 目录可能被并发删除/移动，部分子项失败可接受（后续事件或兜底重建收敛）
        }

        return any;
    }

    private bool RemoveEntry(string fullPath)
    {
        string rel = SyncPath.ToRelativePath(_syncRoot, fullPath);
        lock (_gate)
        {
            bool wasDir = _dirs.Contains(rel);
            bool any = _files.Remove(rel);
            any |= _dirs.Remove(rel);

            // 目录删除/重命名：清除整棵旧前缀子树（子项事件可能不逐条触发）
            if (wasDir)
            {
                string prefix = rel + "/";
                any |= _files.RemoveWhere(p => p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) > 0;
                any |= _dirs.RemoveWhere(p => p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) > 0;
            }

            return any;
        }
    }

    private void BumpVersion()
    {
        Interlocked.Increment(ref _version);
        Changed?.Invoke();
    }

    // ============================ 路径判定 ============================

    /// <summary>快速排除元数据目录（高频 DB 写入路径）与 Office 临时文件，避免逐事件正则匹配。</summary>
    private bool IsExcludedPath(string fullPath)
    {
        string trimmedRoot = _syncRoot.TrimEnd('\\', '/');
        if (fullPath.StartsWith(trimmedRoot + @"\.cloudpan", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return Path.GetFileName(fullPath).StartsWith('~');
    }

    /// <summary>判断全路径是否位于同步根内（GetRelativePath 结果不以上级跳转 .. 开头）。</summary>
    private bool IsInsideSyncRoot(string fullPath)
    {
        try
        {
            string relative = Path.GetRelativePath(_syncRoot, fullPath);
            return !Path.IsPathRooted(relative)
                && relative != ".."
                && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false; // 解析失败按越界处理，忽略该事件
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        // 先取消事件订阅再释放 watcher，防止释放期间事件回调触发（CP300 要求 -=- 成对）
        if (_watcher != null)
        {
            _watcher.Created -= OnCreated;
            _watcher.Deleted -= OnDeleted;
            _watcher.Renamed -= OnRenamed;
            _watcher.Error -= OnWatcherError;
            _watcher.Dispose();
        }
        if (_ignoreWatcher != null)
        {
            _ignoreWatcher.Changed -= OnIgnoreFileChanged;
            _ignoreWatcher.Created -= OnIgnoreFileChanged;
            _ignoreWatcher.Dispose();
        }
        _reconcileTimer?.Dispose();
        Changed = null; // 7.4：事件在多线程订阅/退订时置 null 清理
    }
}
