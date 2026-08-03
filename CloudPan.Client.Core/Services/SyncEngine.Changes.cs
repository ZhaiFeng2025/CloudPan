using CloudPan.Contract;
using CloudPan.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CloudPan.Client.Core.Services;

/// <summary>SyncEngine 部分实现：WebSocket 变更事件与变更入队。</summary>
public partial class SyncEngine
{
    // ============================================================
    // WebSocket 推送事件处理（具名方法，供 Dispose 取消订阅）
    // ============================================================

    private void OnWsFileChanged(string path)
    {
        _logger.LogInformation("WS 推送触发增量同步: {Path}", path);
        TriggerWsIncrementalSync();
    }

    private void OnWsFileDeleted(string path)
    {
        _logger.LogInformation("WS 推送删除: {Path}", path);
        // 按 path 精确处理：直接删除本地副本（不再仅触发增量同步等待树墓碑）。
        // Task.Run 包裹异步删除，避免 async void 异常逃逸；最后兜底触发增量同步（目录删除需拉子树墓碑）。
        Task.Run(async () =>
        {
            try
            {
                await DeleteLocalCopyAsync(path);
                _logger.LogInformation("WS 删除已处理，本地副本已删: {Path}", path);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "WS 删除本地副本失败: {Path}", path);
            }
            finally
            {
                TriggerWsIncrementalSync();
            }
        });
    }

    /// <summary>删除本地副本 + 清理快照与待处理队列（WS file_deleted 精确处理与树墓碑共用）。</summary>
    private async Task DeleteLocalCopyAsync(string path)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        // 取消该路径待处理的上传/下载（远端已删除，本地未决传输不再有意义）
        var pending = await db.SyncQueue
            .Where(q => q.FilePath == path
                && (q.Operation == (int)SyncOperation.Upload || q.Operation == (int)SyncOperation.Download))
            .ToListAsync();
        if (pending.Count > 0)
        {
            db.SyncQueue.RemoveRange(pending);
        }

        string localPath = ToLocalPath(path);
        if (File.Exists(localPath))
        {
            SafeDelete(localPath);
        }

        var snapshot = await db.RemoteSnapshots.FindAsync(path);
        if (snapshot != null)
        {
            db.RemoteSnapshots.Remove(snapshot);
        }

        await db.SaveChangesAsync();
    }

    private void OnWsFileRenamed(string oldPath, string newPath)
    {
        _logger.LogInformation("WS 推送重命名: {OldPath} → {NewPath}", oldPath, newPath);
        TriggerWsIncrementalSync();
    }

    /// <summary>使用锁序列化增量同步调用，避免 WS 推送并发导致重复入队。</summary>
    private void TriggerWsIncrementalSync()
    {
        Task.Run(async () =>
        {
            try
            {
                await _syncLock.WaitAsync();
                try { await IncrementalSyncAsync(CancellationToken.None); }
                catch (Exception ex) { _logger.LogWarning(ex, "WS 触发同步异常"); }
                finally { _syncLock.Release(); }
            }
            catch (Exception ex) { _logger.LogError(ex, "WS 触发同步调度异常"); }
        });
    }

    /// <summary>将重命名操作入队。</summary>
    public async Task EnqueueRenameAsync(string oldPath, string newPath)
    {
        // 忽略规则匹配的路径（内置 *.tmp 等）：原子写入的 tmp→目标 重命名不应同步
        if (SyncIgnoreParser.ShouldIgnore(oldPath, _ignorePatterns)
            || SyncIgnoreParser.ShouldIgnore(newPath, _ignorePatterns))
        {
            _logger.LogDebug("忽略匹配忽略规则的重命名: {Old} → {New}", oldPath, newPath);
            return;
        }

        // T-054：排除集覆盖上传方向——重命名属上传方向变更，排除子树内不对外发布（服务端条目保留原路径）
        if (!IsPathSelected(oldPath) || !IsPathSelected(newPath))
        {
            _logger.LogDebug("路径在排除子树内，跳过重命名入队: {Old} → {New}", oldPath, newPath);
            return;
        }

        await using var db = await _dbFactory.CreateDbContextAsync();
        // 去重：同路径已有的重命名
        var existing = await db.SyncQueue
            .FirstOrDefaultAsync(q => q.FilePath == oldPath && q.Operation == (int)SyncOperation.Rename);
        if (existing != null) { existing.TargetPath = newPath; await db.SaveChangesAsync(); return; }

        db.SyncQueue.Add(new SyncQueue
        {
            FilePath = oldPath,
            Operation = (int)SyncOperation.Rename,
            Priority = (int)QueuePriority.High,
            TargetPath = newPath
        });
        await db.SaveChangesAsync();
        _logger.LogInformation("入队重命名: {Old} → {New}", oldPath, newPath);
    }

    /// <summary>将本地文件变更加入上传队列。</summary>
    public async Task EnqueueLocalChangeAsync(string relativePath, SyncOperation operation)
    {
        // 忽略规则匹配的路径（内置 *.tmp 等 + 用户 .syncignore）：原子写入的临时文件不应同步上传
        if (SyncIgnoreParser.ShouldIgnore(relativePath, _ignorePatterns))
        {
            _logger.LogDebug("忽略匹配忽略规则的变更: {Path}", relativePath);
            return;
        }

        // T-054：排除集覆盖上传方向——排除子树内的本地变更（上传/删除）不入队，
        // 隐私文件不外传，本地副本保留（CloudOnly 残留副本由 FullScan 跳过不重传）。
        // 删除同样拦截：排除目录内删除本地残留副本不得删服务端副本，重新勾选后可再下载恢复。
        if (!IsPathSelected(relativePath))
        {
            _logger.LogDebug("路径在排除子树内，跳过入队: {Op} {Path}", operation, relativePath);
            return;
        }

        await using var db = await _dbFactory.CreateDbContextAsync();

        // 如果是删除操作，取消同一文件待处理的上传/下载
        if (operation == SyncOperation.Delete)
        {
            var pending = await db.SyncQueue
                .Where(q => q.FilePath == relativePath
                    && (q.Operation == (int)SyncOperation.Upload || q.Operation == (int)SyncOperation.Download))
                .ToListAsync();
            db.SyncQueue.RemoveRange(pending);
        }

        // 去重：相同操作已在队列中
        var existing = await db.SyncQueue
            .FirstOrDefaultAsync(q => q.FilePath == relativePath && q.Operation == (int)operation);
        if (existing != null)
        {
            return;
        }

        // 上传去重：文件大小与快照一致 → 进一步比对哈希；均未变则跳过
        long fileSize = 0;
        // 上传冲突检测基准版本（F-06）：本地上一次已同步的服务端版本，服务端据此检测并发编辑
        int? baseVersion = null;
        if (operation == SyncOperation.Upload)
        {
            string fullPath = NormalizePath(Path.Combine(_syncRoot, relativePath.TrimStart('/')));

            // T-046：目录单独入队 mkdir——目录不是文件，走 ProcessMkdirAsync 建立服务端条目而非丢弃
            if (Directory.Exists(fullPath))
            {
                // 快照已存在且为目录 → 已同步，跳过重复入队
                var dirSnapshot = await db.RemoteSnapshots.FindAsync(relativePath);
                if (dirSnapshot != null && dirSnapshot.Type == (int)FileType.Directory)
                {
                    _logger.LogDebug("目录已同步，跳过 mkdir 入队: {Path}", relativePath);
                    return;
                }

                db.SyncQueue.Add(new SyncQueue
                {
                    FilePath = relativePath,
                    Operation = (int)operation,
                    Priority = (int)QueuePriority.High,
                    FileSize = 0
                });
                await db.SaveChangesAsync();
                _logger.LogInformation($"入队: {operation} {relativePath}");
                return;
            }

            if (!File.Exists(fullPath))
            {
                return;
            }

            var snapshot = await db.RemoteSnapshots.FindAsync(relativePath);
            baseVersion = snapshot?.Version; // 记录 BaseVersion = snapshot.Version（本地上一次已同步版本）
            long localSize = new FileInfo(fullPath).Length;
            if (snapshot != null && localSize == snapshot.Size)
            {
                // 大小相同，进一步比对哈希确认无变化
                if (!string.IsNullOrEmpty(snapshot.Hash))
                {
                    // 哈希比对：大小相同且哈希一致 → 真实变更
                    string localHash = await FileHasher.ComputeSha256Async(fullPath);
                    if (string.Equals(localHash, snapshot.Hash, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogInformation("跳过上传（大小和哈希均未变）: {Path}", relativePath);
                        return;
                    }
                    _logger.LogInformation("大小相同但哈希不同，仍需上传: {Path}", relativePath);
                }
                else
                {
                    // 快照无哈希记录（旧版数据库迁移场景），执行上传以确保内容一致
                    _logger.LogInformation("快照无哈希记录，执行上传以确保内容一致: {Path}", relativePath);
                }
            }
            fileSize = localSize;
        }

        db.SyncQueue.Add(new SyncQueue
        {
            FilePath = relativePath,
            Operation = (int)operation,
            Priority = fileSize < QueuePriorityThreshold ? (int)QueuePriority.High : (int)QueuePriority.Normal,
            FileSize = fileSize,
            BaseVersion = baseVersion // 冲突检测基准版本，ProcessUploadAsync 携带给服务端触发 409
        });
        await db.SaveChangesAsync();
        _logger.LogInformation($"入队: {operation} {relativePath}");
    }

    /// <summary>
    /// 上传入口（T-033）：将本地文件复制到同步目录并纳入上传队列（普通/分块由队列处理）。
    /// <paramref name="destRelativeDir"/> 为同步树内的相对目录（"/" 为同步根）；目标重名时覆盖，视为新版本上传。
    /// 供文件浏览视图「上传」按钮与主窗口拖拽导入复用。
    /// </summary>
    public async Task ImportFilesAsync(IReadOnlyList<string> sourceFiles, string destRelativeDir = "/", CancellationToken ct = default)
    {
        // 防御：目标目录须为同步树内路径（拒绝上级跳转穿越同步根）
        string cleanDir = destRelativeDir.Replace('\\', '/').TrimEnd('/');
        if (cleanDir.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(seg => seg == ".."))
        {
            _logger.LogWarning("拒绝导入：目标目录含上级跳转: {Dir}", destRelativeDir);
            return;
        }

        foreach (string source in sourceFiles)
        {
            ct.ThrowIfCancellationRequested();

            string fileName = Path.GetFileName(source);
            string destRel = "/" + (cleanDir + "/" + fileName).TrimStart('/');
            string destAbs = ToLocalPath(destRel);
            Directory.CreateDirectory(Path.GetDirectoryName(destAbs)!);

            // 复制到同步目录后入队上传；FileWatcher 若已入队同操作，EnqueueLocalChangeAsync 去重，无重复上传
            await Task.Run(() => File.Copy(source, destAbs, overwrite: true), ct);
            await EnqueueLocalChangeAsync(destRel, SyncOperation.Upload);

            _logger.LogInformation("导入文件入队上传: {Source} → {Dest}", source, destRel);
        }
    }
}
