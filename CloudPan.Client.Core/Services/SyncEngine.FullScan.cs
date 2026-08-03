using CloudPan.Contract;
using CloudPan.Infrastructure.Models;
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

        await using var store = await _storeFactory.CreateStoreAsync(ct);

        // 1. 枚举本地所有文件及目录（忽略 .cloudpan 和临时文件），单次遍历替代原先两次独立遍历。
        // T-046：目录也纳入枚举（含空目录），用于快照匹配与缺失目录的 mkdir 入队。
        HashSet<string> localFiles = new HashSet<string>();
        HashSet<string> localDirs = new HashSet<string>();

        if (Directory.Exists(NormalizePath(_syncRoot)))
        {
            foreach (string fullPath in Directory.EnumerateFileSystemEntries(NormalizePath(_syncRoot), "*", SearchOption.AllDirectories))
            {
                if (ShouldIgnoreScan(fullPath))
                {
                    continue;
                }

                string rel = ToRelativePath(fullPath);
                if (Directory.Exists(fullPath))
                {
                    localDirs.Add(rel);
                    continue;
                }

                localFiles.Add(rel);
            }
        }

        // 2. 分批加载远端快照（每次 1000 条），避免全量加载到内存
        const int batchSize = 1000;
        // T-046：已匹配的本地路径（文件+目录）——含快照则第 3 步不再重复入队
        HashSet<string> matchedLocalPaths = new HashSet<string>();
        // 选择性同步（F-23）：CloudOnly 快照路径集合——取消勾选后本地仍残留副本的文件不作为新文件上传
        HashSet<string> cloudOnlyPaths = new HashSet<string>();

        // T-066：目录重命名对齐——收集未决重命名（旧路径 → 目标路径）。全量扫描落在重命名队列
        // 未决窗口（本地已改名、Move 尚未处理）时，不得把 rename 判为 delete+create：
        // 旧前缀快照本地缺失不入队 Delete（消除旧路径 404 删除噪音 + Delete 先于 Move 的回收站误删竞态），
        // 新前缀本地文件不入队 Upload（重命名已由 ProcessRenameAsync 收敛快照，避免整棵子树重复上传）。
        List<string> pendingRenameOldPaths = new();
        List<string> pendingRenameNewPaths = new();
        foreach (var rename in await store.GetQueuesByOperationAsync((int)SyncOperation.Rename, ct))
        {
            if (!string.IsNullOrEmpty(rename.TargetPath))
            {
                pendingRenameOldPaths.Add(rename.FilePath);
                pendingRenameNewPaths.Add(rename.TargetPath);
            }
        }

        int snapshotCount = 0;

        List<RemoteSnapshot> batch;
        do
        {
            batch = await store.GetSnapshotsPagedAsync(snapshotCount, batchSize, ct);

            foreach (var snapshot in batch)
            {
                // 跳过 CloudOnly 文件（不含本地副本，不纳入删除检测，由用户按需下载）
                if (snapshot.State == (int)CloudPan.Contract.FileState.CloudOnly)
                {
                    // T-054：重新勾选已排除目录——IsPathSelected 转 true 的 CloudOnly 快照恢复同步，
                    // 否则保持排除态（跳过并记录供第 3 步跳过本地残留副本）
                    if (IsPathSelected(snapshot.Path))
                    {
                        if (!localFiles.Contains(snapshot.Path))
                        {
                            // 本地缺失 → 入队下载（CloudOnly 从未落盘，版本相等也需下载）
                            if (snapshot.Type == (int)CloudPan.Contract.FileType.File)
                            {
                                _logger.LogInformation("重新勾选已排除目录，本地缺失，入队下载: {Path}", snapshot.Path);
                                await EnqueueDownloadAsync(snapshot.Path, snapshot.Version);
                            }
                            continue;
                        }

                        // 本地残留副本存在 → 恢复 State（重置 CloudOnly → Synced），本地内容即同步内容；
                        // 不 continue：落入下方常规大小/哈希比对——本地副本若在排除期间被修改（内容漂移）→ 入队上传
                        snapshot.State = (int)CloudPan.Contract.FileState.Synced;
                        snapshot.IsDownloaded = true;
                        _logger.LogInformation("重新勾选已排除目录，本地副本恢复同步: {Path}", snapshot.Path);
                        await store.CommitAsync();
                    }
                    else
                    {
                        // 记录该路径已取消勾选，供第 3 步跳过本地残留副本
                        cloudOnlyPaths.Add(snapshot.Path);
                        continue;
                    }
                }

                if (!localFiles.Contains(snapshot.Path) && !localDirs.Contains(snapshot.Path))
                {
                    // T-066：目录重命名未决窗口——本地缺失可能只是重命名（本地已改名、Move 未处理），
                    // 该路径处于未决重命名旧前缀 → 不入队 Delete（服务端已/将移动），
                    // 消除旧路径 404 删除噪音与 Delete 先于 Move 到达的回收站误删竞态。
                    if (pendingRenameOldPaths.Count > 0 && IsUnderAnyPrefix(snapshot.Path, pendingRenameOldPaths))
                    {
                        _logger.LogDebug("路径处于未决重命名旧前缀，跳过删除判定: {Path}", snapshot.Path);
                        continue;
                    }

                    // 本地缺失的删除判定（F-37/T-037）：只对『曾落盘且当前缺失』的文件入队 Delete。
                    // 未完成首次下载的快照（IsDownloaded=false）不触发删除传播——远端新文件在下载窗口内
                    // 快照已建但本地无文件，若按旧逻辑判定本地删除会取消未决下载并把服务端唯一副本移入回收站。
                    if (snapshot.Type == (int)CloudPan.Contract.FileType.File
                        && snapshot.IsDownloaded)
                    {
                        // 该路径存在未决下载项 → 下载窗口内跳过删除判定，待下载完成后再判定
                        bool hasPendingDownload = await store.HasPendingDownloadAsync(snapshot.Path, ct);
                        if (!hasPendingDownload)
                        {
                            _logger.LogInformation("全量扫描检测到本地删除: {Path}", snapshot.Path);
                            await EnqueueLocalChangeAsync(snapshot.Path, SyncOperation.Delete);
                        }
                    }
                    // T-049：目录删除兜底——本地目录缺失且快照为目录且『曾在本机物化』(IsDownloaded=true)
                    // → 入队 Delete，目录删除有 5 分钟兜底扫描（不再只依赖 FileSystemWatcher）。
                    // 远端目录快照（IsDownloaded=false，未物化）不触发，防止空目录首次同步未物化时
                    // 被误判为本地删除 → 删除-重建振荡（F-49）。
                    else if (snapshot.Type == (int)CloudPan.Contract.FileType.Directory
                        && snapshot.IsDownloaded)
                    {
                        _logger.LogInformation("全量扫描检测到本地目录删除: {Path}", snapshot.Path);
                        await EnqueueLocalChangeAsync(snapshot.Path, SyncOperation.Delete);
                    }
                    continue;
                }

                matchedLocalPaths.Add(snapshot.Path);

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
            if (matchedLocalPaths.Contains(path))
            {
                continue;
            }

            // T-066：目录重命名未决窗口——本地新路径文件可能是未决重命名的目标（快照将随 Move 收敛），
            // 不得作为新文件判为 create 上传，否则 rename 被误判为 delete+create，整棵子树重复上传。
            if (pendingRenameNewPaths.Count > 0 && IsUnderAnyPrefix(path, pendingRenameNewPaths))
            {
                _logger.LogDebug("路径处于未决重命名新前缀，跳过新文件上传: {Path}", path);
                continue;
            }

            // 选择性同步（F-23）：跳过 State==CloudOnly 快照对应的本地文件——取消勾选后本地残留副本
            // 若当新文件上传会置回 Synced→下次增量同步打回 CloudOnly→下次扫描重传，形成振荡
            if (cloudOnlyPaths.Contains(path))
            {
                continue;
            }

            // T-054：排除集覆盖上传方向——排除子树内新建的本地文件（无快照）不入队上传
            if (!IsPathSelected(path))
            {
                _logger.LogDebug("路径在排除子树内，跳过新文件上传: {Path}", path);
                continue;
            }

            await EnqueueLocalChangeAsync(path, SyncOperation.Upload);
        }

        // T-046：目录补齐——本地目录无快照 → 入队 mkdir（服务端 MkdirAsync 建立目录条目，空目录在其他设备可见）。
        // 目录快照不会被标记 CloudOnly（ApplyRemoteChanges 目录分支先于 IsPathSelected），故无需 cloudOnlyPaths 过滤。
        foreach (string path in localDirs)
        {
            if (matchedLocalPaths.Contains(path))
            {
                continue;
            }

            // T-066：目录重命名未决窗口——新前缀本地目录同理跳过 mkdir（rename 目标目录随 Move 建立）
            if (pendingRenameNewPaths.Count > 0 && IsUnderAnyPrefix(path, pendingRenameNewPaths))
            {
                _logger.LogDebug("路径处于未决重命名新前缀，跳过目录同步: {Path}", path);
                continue;
            }

            // T-054：排除子树内新建的本地目录（无快照）不入队 mkdir
            if (!IsPathSelected(path))
            {
                _logger.LogDebug("路径在排除子树内，跳过目录同步: {Path}", path);
                continue;
            }

            await EnqueueLocalChangeAsync(path, SyncOperation.Upload);
        }

        _logger.LogInformation("全量扫描完成（文件: {FileCount}, 目录: {DirCount}, 快照: {SnapshotCount}）",
            localFiles.Count, localDirs.Count, snapshotCount);
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
        => SyncPath.ToLocalPath(_syncRoot, relativePath);

    /// <summary>
    /// T-085：统一委托 SyncPath.NormalizePath（含加长路径前缀前消解 .. 段），避免散落副本遗漏防线。
    /// </summary>
    private static string NormalizePath(string path)
        => SyncPath.NormalizePath(path);

    /// <summary>检查路径是否在已选择的同步范围内（排除集语义，T-047）。</summary>
    /// <remarks>
    /// SelectedPaths 语义（v2 排除集）：
    /// - 空集合 → 显式全不同步（取消全选后不回退为 { "/" } 全选）。
    /// - 含 "/"（全选默认值，含 v1.0.0 旧版选择集恒含根节点）→ 全选，不排除任何路径。
    /// - 其余 → 排除子树列表：命中任一排除子树（含深层路径）→ 不同步。
    /// </remarks>
    private bool IsPathSelected(string path)
    {
        // 局部快照：读取一次引用，单次调用内语义一致（热更新替换引用不影响本次判断，T-063）
        List<string> selectedPaths = _selectedPaths;

        // 空集合 = 显式全不同步（不再回退为 { "/" } 全选）
        if (selectedPaths.Count == 0)
        {
            return false;
        }

        // 含 "/"（全选默认值 / v1.0.0 旧版选择集恒含根节点）→ 全选
        if (selectedPaths.Contains("/"))
        {
            return true;
        }

        // 排除集：命中任一排除子树 → 不同步
        string normalized = path.TrimEnd('/') + "/";
        bool excluded = selectedPaths.Any(sp =>
        {
            string p = sp.TrimEnd('/') + "/";
            return normalized.StartsWith(p, StringComparison.OrdinalIgnoreCase)
                   || path.Equals(sp.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
        });
        return !excluded;
    }

    /// <summary>
    /// T-066：判断路径是否位于任一前缀（未决重命名的旧前缀/新前缀）覆盖的子树内。
    /// 前缀归一化为目录边界（"/photos" → "/photos/"），避免误伤相似路径（"/photosx"）。
    /// </summary>
    private static bool IsUnderAnyPrefix(string path, IReadOnlyList<string> prefixes)
    {
        string normalized = path.TrimEnd('/') + "/";
        foreach (string prefix in prefixes)
        {
            string p = prefix.TrimEnd('/') + "/";
            if (normalized.StartsWith(p, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }
}
