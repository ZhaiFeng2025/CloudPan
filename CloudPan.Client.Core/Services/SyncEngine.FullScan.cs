using CloudPan.Client.Core.Models;
using CloudPan.Contract;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CloudPan.Client.Core.Services;

/// <summary>SyncEngine 部分实现：全量扫描本地文件（FileSystemWatcher 兜底通道）与路径/选择工具。</summary>
public partial class SyncEngine
{
    // ============================================================
    // 工具方法
    // ============================================================

    /// <summary>
    /// 全量扫描本地文件，与远端快照对比，入队差异项。
    /// 作为 FileSystemWatcher 的兜底通道，每 5 分钟调用一次。
    /// </summary>
    public async Task FullScanAsync(CancellationToken ct = default)
    {
        // 并发控制：与增量同步/WS 触发扫描互斥，避免并发重复入队（FileWatcher 定时器与主循环共用此入口）
        if (!await _syncLock.WaitAsync(0))
        {
            _logger.LogDebug("全量扫描跳过——已有同步任务在运行");
            return;
        }

        try
        {
            await FullScanCoreAsync(ct);
        }
        finally
        {
            _syncLock.Release();
        }
    }

    /// <summary>全量扫描核心逻辑（由 FullScanAsync 持锁调用）。</summary>
    private async Task FullScanCoreAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("定时全量扫描开始...");
        NotifyStatus("全量扫描中...");

        EnsureRootExists();

        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        // 1. 枚举本地所有文件及目录（忽略 .cloudpan 和临时文件），单次遍历替代原先两次独立遍历
        HashSet<string> localFiles = new HashSet<string>();

        if (Directory.Exists(NormalizePath(_syncRoot)))
        {
            foreach (string fullPath in Directory.EnumerateFileSystemEntries(NormalizePath(_syncRoot), "*", SearchOption.AllDirectories))
            {
                if (ShouldIgnoreScan(fullPath))
                {
                    continue;
                }

                if (Directory.Exists(fullPath))
                {
                    continue; // 只处理文件，跳过目录
                }

                string rel = ToRelativePath(fullPath);
                localFiles.Add(rel);
            }
        }

        // 2. 分批加载远端快照（每次 1000 条），避免全量加载到内存
        const int batchSize = 1000;
        HashSet<string> matchedLocalFiles = new HashSet<string>();
        // 选择性同步（F-23）：CloudOnly 快照路径集合——取消勾选后本地仍残留副本的文件不作为新文件上传
        HashSet<string> cloudOnlyPaths = new HashSet<string>();
        int snapshotCount = 0;

        List<RemoteSnapshot> batch;
        do
        {
            batch = await db.RemoteSnapshots
                .OrderBy(s => s.Path)
                .Skip(snapshotCount)
                .Take(batchSize)
                .ToListAsync(ct);

            foreach (var snapshot in batch)
            {
                // 跳过 CloudOnly 文件（不含本地副本，不纳入删除检测，由用户按需下载）
                if (snapshot.State == (int)CloudPan.Contract.FileState.CloudOnly)
                {
                    // 记录该路径已取消勾选，供第 3 步跳过本地残留副本
                    cloudOnlyPaths.Add(snapshot.Path);
                    continue;
                }

                if (!localFiles.Contains(snapshot.Path))
                {
                    // 本地缺失的删除判定（F-37/T-037）：只对『曾落盘且当前缺失』的文件入队 Delete。
                    // 未完成首次下载的快照（IsDownloaded=false）不触发删除传播——远端新文件在下载窗口内
                    // 快照已建但本地无文件，若按旧逻辑判定本地删除会取消未决下载并把服务端唯一副本移入回收站。
                    if (snapshot.Type == (int)CloudPan.Contract.FileType.File
                        && snapshot.IsDownloaded)
                    {
                        // 该路径存在未决下载项 → 下载窗口内跳过删除判定，待下载完成后再判定
                        bool hasPendingDownload = await db.SyncQueue
                            .AnyAsync(q => q.FilePath == snapshot.Path
                                && q.Operation == (int)SyncOperation.Download, ct);
                        if (!hasPendingDownload)
                        {
                            _logger.LogInformation("全量扫描检测到本地删除: {Path}", snapshot.Path);
                            await EnqueueLocalChangeAsync(snapshot.Path, SyncOperation.Delete);
                        }
                    }
                    continue;
                }

                matchedLocalFiles.Add(snapshot.Path);

                // 文件：大小对比 + 哈希对比
                if (snapshot.Type == (int)CloudPan.Contract.FileType.File)
                {
                    string fullPath = ToLocalPath(snapshot.Path);
                    long localSize = new FileInfo(fullPath).Length;
                    if (localSize != snapshot.Size)
                    {
                        _logger.LogInformation("全量扫描检测到变更: {Path} ({OldSize} → {NewSize})",
                            snapshot.Path, snapshot.Size, localSize);
                        await EnqueueLocalChangeAsync(snapshot.Path, SyncOperation.Upload);
                    }
                    else if (!string.IsNullOrEmpty(snapshot.Hash))
                    {
                        // 大小相同，进一步比对哈希确认无变化
                        string localHash = await FileHasher.ComputeSha256Async(fullPath);
                        if (!string.Equals(localHash, snapshot.Hash, StringComparison.OrdinalIgnoreCase))
                        {
                            _logger.LogInformation("全量扫描检测到变更（哈希不同）: {Path}", snapshot.Path);
                            await EnqueueLocalChangeAsync(snapshot.Path, SyncOperation.Upload);
                        }
                    }
                }
            }

            snapshotCount += batch.Count;
        }
        while (batch.Count == batchSize && !ct.IsCancellationRequested);

        // 3. 无快照的本地文件 → 新文件，入队上传
        foreach (string path in localFiles)
        {
            if (matchedLocalFiles.Contains(path))
            {
                continue;
            }

            // 选择性同步（F-23）：跳过 State==CloudOnly 快照对应的本地文件——取消勾选后本地残留副本
            // 若当新文件上传会置回 Synced→下次增量同步打回 CloudOnly→下次扫描重传，形成振荡
            if (cloudOnlyPaths.Contains(path))
            {
                continue;
            }

            await EnqueueLocalChangeAsync(path, SyncOperation.Upload);
        }

        _logger.LogInformation("全量扫描完成（文件: {FileCount}, 快照: {SnapshotCount}）",
            localFiles.Count, snapshotCount);
        if (_firstSyncActive)
        {
            NotifyStatus($"全量扫描本地文件 — {localFiles.Count} 项");
        }
        else
        {
            NotifyStatus($"全量扫描完成 — {localFiles.Count} 项本地文件");
        }
    }

    private string ToRelativePath(string fullPath)
    {
        // 去除 \\?\ 前缀（如有），确保与 _syncRoot 格式一致
        string cleanFull = fullPath.StartsWith(@"\\?\") ? fullPath[4..] : fullPath;
        string cleanRoot = _syncRoot.StartsWith(@"\\?\") ? _syncRoot[4..] : _syncRoot;
        string relative = Path.GetRelativePath(cleanRoot, cleanFull);
        return "/" + relative.Replace('\\', '/');
    }

    private bool ShouldIgnoreScan(string fullPath)
    {
        string relativePath = ToRelativePath(fullPath);
        return SyncIgnoreParser.ShouldIgnore(relativePath, _ignorePatterns);
    }

    private string ToLocalPath(string relativePath)
    {
        string path = Path.Combine(_syncRoot, relativePath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
        return NormalizePath(path);
    }

    /// <summary>
    /// 为路径添加 \\?\ 前缀以支持长路径（超过 MAX_PATH 260 字符）。
    /// 对支持的所有文件 I/O 操作使用此方法包装路径。
    /// </summary>
    private static string NormalizePath(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return path;
        }
        // 已包含 \\?\ 前缀则跳过
        if (path.StartsWith(@"\\?\"))
        {
            return path;
        }
        // 只对绝对本地路径（如 C:\...）添加前缀
        if (path.Length >= 3 && path[1] == ':' && path[2] == '\\')
        {
            return @"\\?\" + path;
        }
        // UNC 路径（\\server\share）转换为 \\?\UNC\ 格式
        if (path.StartsWith(@"\\"))
        {
            return @"\\?\UNC\" + path[2..];
        }

        return path;
    }

    /// <summary>检查路径是否在已选择的同步范围内。</summary>
    private bool IsPathSelected(string path)
    {
        if (_selectedPaths.Count == 1 && _selectedPaths[0] == "/")
        {
            return true; // 全选
        }

        string normalized = path.TrimEnd('/') + "/";
        return _selectedPaths.Any(sp =>
        {
            string p = sp.TrimEnd('/') + "/";
            return normalized.StartsWith(p, StringComparison.OrdinalIgnoreCase)
                   || path.Equals(sp.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
        });
    }
}
