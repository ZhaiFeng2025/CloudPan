using CloudPan.Contract;
using CloudPan.Infrastructure.Storage;
using Microsoft.Extensions.Logging;

namespace CloudPan.Client.Core.Services;

/// <summary>
/// BrowseLocalIndex 部分实现（T-108 拆分）：FileSystemWatcher 装配、文件系统事件增量维护与全量重建。
/// 主类声明/集合维护/路径判定见 BrowseLocalIndex.cs。
/// </summary>
internal sealed partial class BrowseLocalIndex
{
    /// <summary>重建本地索引（清空 + 全量枚举 + 重启监控）。notify=true 时触发变更通知（初始构建为 false）。</summary>
    private void RebuildCore(bool notify)
    {
        _watcher?.Dispose();
        _watcher = null;

        var newFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var newDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(SyncPath.NormalizePath(_syncRoot)))
        {
            foreach (string fullPath in Directory.EnumerateFileSystemEntries(SyncPath.NormalizePath(_syncRoot), "*", SearchOption.AllDirectories))
            {
                if (SyncPath.ShouldIgnore(_syncRoot, fullPath, _ignorePatterns))
                {
                    continue;
                }

                string rel = SyncPath.ToRelativePath(_syncRoot, fullPath);
                if (Directory.Exists(fullPath))
                {
                    newDirs.Add(rel);
                }
                else
                {
                    newFiles.Add(rel);
                }
            }
        }

        lock (_gate)
        {
            _files = newFiles;
            _dirs = newDirs;
        }

        StartWatcher();
        StartIgnoreWatcher();
        if (notify)
        {
            BumpVersion();
        }
    }

    private void StartWatcher()
    {
        _watcher?.Dispose();
        _watcher = new FileSystemWatcher(_syncRoot)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
            InternalBufferSize = 65536 // 64KB，与 FileWatcherService 一致
        };
        _watcher.Created += OnCreated;
        _watcher.Deleted += OnDeleted;
        _watcher.Renamed += OnRenamed;
        _watcher.Error += OnWatcherError;
        _watcher.EnableRaisingEvents = true;
    }

    private void StartIgnoreWatcher()
    {
        _ignoreWatcher?.Dispose();
        _ignoreWatcher = new FileSystemWatcher(_syncRoot, ".syncignore")
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.CreationTime,
            EnableRaisingEvents = true
        };
        _ignoreWatcher.Changed += OnIgnoreFileChanged;
        _ignoreWatcher.Created += OnIgnoreFileChanged;
    }

    /// <summary>兜底重建定时器（间隔单源：shared-spec.json → SpecConfig.ScanIntervalMinutes）。</summary>
    private void StartReconcileTimer()
    {
        _reconcileTimer?.Dispose();
        var interval = TimeSpan.FromMinutes(Math.Max(1, SpecConfig.ScanIntervalMinutes));
        _reconcileTimer = new System.Threading.Timer(_ =>
        {
            // CLAUDE.md 7.2：Timer 回调不得 fire-and-forget，须 Task.Run + 全量 try-catch
            Task.Run(() =>
            {
                try
                {
                    RebuildCore(notify: true);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "浏览缓存定时重建异常");
                }
            });
        }, null, interval, interval);
    }

    // ============================ 文件系统事件 ============================

    private void OnCreated(object sender, FileSystemEventArgs e)
    {
        try
        {
            if (!IsInsideSyncRoot(e.FullPath) || IsExcludedPath(e.FullPath))
            {
                return;
            }

            bool any;
            if (Directory.Exists(e.FullPath))
            {
                // 新目录：递归补齐子树（覆盖「整棵移入」仅触发顶层事件的场景）
                any = SyncPath.ShouldIgnore(_syncRoot, e.FullPath, _ignorePatterns)
                    ? false
                    : AddSubtree(e.FullPath);
            }
            else
            {
                any = AddEntry(e.FullPath);
            }

            if (any)
            {
                BumpVersion();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "浏览缓存处理创建事件异常: {Path}", e.FullPath);
        }
    }

    private void OnDeleted(object sender, FileSystemEventArgs e)
    {
        try
        {
            if (!IsInsideSyncRoot(e.FullPath) || IsExcludedPath(e.FullPath))
            {
                return;
            }

            if (RemoveEntry(e.FullPath))
            {
                BumpVersion();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "浏览缓存处理删除事件异常: {Path}", e.FullPath);
        }
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        try
        {
            if (!IsInsideSyncRoot(e.FullPath) || !IsInsideSyncRoot(e.OldFullPath))
            {
                return;
            }

            bool removed = RemoveEntry(e.OldFullPath);
            bool added = false;
            if (Directory.Exists(e.FullPath))
            {
                if (!SyncPath.ShouldIgnore(_syncRoot, e.FullPath, _ignorePatterns))
                {
                    added = AddSubtree(e.FullPath);
                }
            }
            else
            {
                added = AddEntry(e.FullPath);
            }

            if (removed || added)
            {
                BumpVersion();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "浏览缓存处理重命名事件异常: {Old} → {New}", e.OldFullPath, e.FullPath);
        }
    }

    /// <summary>.syncignore 变更：重载规则并按新规则后台重建。</summary>
    private void OnIgnoreFileChanged(object? sender, FileSystemEventArgs e)
    {
        try
        {
            _ignorePatterns = SyncIgnoreParser.LoadFromSyncRoot(_syncRoot);
            Task.Run(() =>
            {
                try
                {
                    if (_initialized)
                    {
                        RebuildCore(notify: true);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "浏览缓存按新忽略规则重建异常");
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "浏览缓存重载忽略规则异常");
        }
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        // 缓冲区溢出/内部错误 → 后台重建兜底（对齐 FileWatcherService 重启语义）
        try
        {
            _logger.LogWarning("浏览缓存 FileSystemWatcher 错误（{Error}），后台重建",
                e.GetException()?.Message ?? "未知");
            Task.Run(() =>
            {
                try
                {
                    if (_initialized)
                    {
                        RebuildCore(notify: true);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "浏览缓存按错误恢复重建异常");
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "浏览缓存错误处理异常");
        }
    }
}
