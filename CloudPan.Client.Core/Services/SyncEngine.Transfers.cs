using System.Globalization;
using CloudPan.Client.Core.Models;
using CloudPan.Contract;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CloudPan.Client.Core.Services;

/// <summary>SyncEngine 部分实现：文件传输（上传/下载/删除/重命名）。</summary>
public partial class SyncEngine
{
    /// <returns>true = 成功，应从队列移除</returns>
    private async Task<bool> ProcessUploadAsync(SyncQueue item, CancellationToken ct)
    {
        string localPath = ToLocalPath(item.FilePath);

        // T-046：目录上传走 mkdir 分支（实现见 SyncEngine.Mkdir.cs，独立文件避免超行数上限）
        if (Directory.Exists(localPath))
        {
            return await ProcessMkdirAsync(item, ct);
        }

        if (!File.Exists(localPath))
        {
            _logger.LogWarning($"上传跳过——文件不存在，移除队列项: {item.FilePath}");
            return true; // 文件已不存在，从队列移除
        }

        string lastModified = File.GetLastWriteTimeUtc(localPath).ToString("O");
        NotifyStatus($"上传 ({_queueCompleted + 1}/{_totalFileCount}): {Path.GetFileName(item.FilePath)}");

        // m-08: 上传前记录文件 Hash，用于检测上传过程中文件是否被修改
        string? preUploadHash = null;
        try { preUploadHash = await FileHasher.ComputeSha256Async(localPath); }
        catch (Exception ex) { _logger.LogWarning(ex, "上传前计算文件哈希失败: {Path}", item.FilePath); }

        UploadResponse? result;
        try
        {
            result = await _api.UploadChunkedAsync(localPath, item.FilePath, item.BaseVersion ?? 0, lastModified, ct: ct);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            // 收集冲突详情
            var localModified = File.GetLastWriteTimeUtc(localPath);
            long localSize = new FileInfo(localPath).Length;

            string? remoteHash = null;
            long? remoteSize = null;
            string? remoteLastModified = null; // T-036：远程真实修改时间来自 /api/tree 快照
            try
            {
                await using var snapDb = await _dbFactory.CreateDbContextAsync(ct);
                var remoteSnapshot = await snapDb.RemoteSnapshots.FindAsync(new object[] { item.FilePath }, ct);
                if (remoteSnapshot != null)
                {
                    remoteHash = remoteSnapshot.Hash;
                    remoteSize = remoteSnapshot.Size;
                    remoteLastModified = remoteSnapshot.LastModified;
                }
            }
            catch (Exception snapEx) { _logger.LogWarning(snapEx, "获取远程快照失败（非关键）"); }

            DateTime? remoteModifiedTime = ParseRemoteLastModified(remoteLastModified);
            ConflictInfo conflictInfo = new ConflictInfo(
                RelativePath: item.FilePath,
                LocalPath: localPath,
                LocalModifiedTime: localModified,
                RemoteModifiedTime: null,
                LocalFileSize: localSize,
                RemoteFileSize: remoteSize,
                RemoteHash: remoteHash
            ) with { RemoteModifiedTime = remoteModifiedTime };

            _pendingConflicts.TryAdd(item.FilePath, conflictInfo);
            ConflictDetected?.Invoke(conflictInfo);
            _logger.LogWarning("上传冲突（409）: {Path} — 服务端版本已变更", item.FilePath);
            return false; // 队列项保留但被 _pendingConflicts 跳过，等待用户决策
        }
        _logger.LogInformation($"上传完成: {item.FilePath} → v{result?.Data.Version}");

        // m-08: 上传完成后再次读取 Hash，检测上传过程中文件是否被修改
        if (preUploadHash != null)
        {
            try
            {
                string postUploadHash = await FileHasher.ComputeSha256Async(localPath);
                if (!string.Equals(preUploadHash, postUploadHash, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning("上传过程中文件被修改，重新入队: {Path}", item.FilePath);
                    await EnqueueLocalChangeAsync(item.FilePath, SyncOperation.Upload);
                    return true; // 移除当前队列项，由新入队的项处理变更后的内容
                }
            }
            catch (FileNotFoundException)
            {
                // 上传后文件已被删除或改名 → 入队删除操作
                _logger.LogWarning("上传后文件已被删除，入队删除: {Path}", item.FilePath);
                await EnqueueLocalChangeAsync(item.FilePath, SyncOperation.Delete);
            }
            catch (Exception ex) { _logger.LogWarning(ex, "上传后计算文件哈希失败: {Path}", item.FilePath); }
        }

        // 上传成功后更新本地快照，避免下次增量同步认为需要重新下载
        await using var db = await _dbFactory.CreateDbContextAsync();
        var snapshot = await db.RemoteSnapshots.FindAsync(item.FilePath);
        if (snapshot != null)
        {
            snapshot.Version = result?.Data.Version ?? snapshot.Version;
            snapshot.Hash = result?.Data.Hash ?? snapshot.Hash;
            snapshot.State = (int)CloudPan.Contract.FileState.Synced;
            snapshot.LastModified = lastModified; // T-036：快照记录远程修改时间（与上传携带值一致）
            snapshot.IsDownloaded = true; // T-037：文件在本地落盘（上传成功）
        }
        else if (result != null)
        {
            db.RemoteSnapshots.Add(new RemoteSnapshot
            {
                Path = item.FilePath,
                Type = (int)CloudPan.Contract.FileType.File,
                Hash = result.Data.Hash,
                Size = result.Data.Size,
                Version = result.Data.Version,
                State = (int)CloudPan.Contract.FileState.Synced,
                LastModified = lastModified,
                IsDownloaded = true // T-037：上传成功即本地已落盘
            });
        }
        await db.SaveChangesAsync();

        return true;
    }

    /// <returns>true = 成功，应从队列移除</returns>
    private async Task<bool> ProcessDownloadAsync(SyncQueue item, CancellationToken ct)
    {
        string localPath = ToLocalPath(item.FilePath);

        // M-02: 下载前检测本地是否已修改
        if (File.Exists(localPath))
        {
            await using var checkDb = await _dbFactory.CreateDbContextAsync(ct);
            var snapshot = await checkDb.RemoteSnapshots.FindAsync(new object[] { item.FilePath }, ct);
            if (snapshot != null && !string.IsNullOrEmpty(snapshot.Hash))
            {
                string currentLocalHash = await FileHasher.ComputeSha256Async(localPath);
                if (!string.Equals(currentLocalHash, snapshot.Hash, StringComparison.OrdinalIgnoreCase))
                {
                    // 本地文件已被修改且未同步，触发冲突
                    var localModified = File.GetLastWriteTimeUtc(localPath);
                    long currentLocalSize = new FileInfo(localPath).Length;

                    DateTime? remoteModifiedTime = ParseRemoteLastModified(snapshot.LastModified); // T-036：来自 /api/tree 快照
                    ConflictInfo conflictInfo = new ConflictInfo(
                        RelativePath: item.FilePath,
                        LocalPath: localPath,
                        LocalModifiedTime: localModified,
                        RemoteModifiedTime: null,
                        LocalFileSize: currentLocalSize,
                        RemoteFileSize: item.FileSize,
                        RemoteHash: snapshot.Hash
                    ) with { RemoteModifiedTime = remoteModifiedTime };

                    _pendingConflicts.TryAdd(item.FilePath, conflictInfo);
                    ConflictDetected?.Invoke(conflictInfo);
                    _logger.LogWarning("下载前检测到本地修改（哈希不匹配），跳过下载: {Path}", item.FilePath);
                    return false; // 保留队列项，等待用户决策
                }
            }
        }

        // 下载前检查磁盘空间（大文件下载前确保有足够空间）
        if (item.FileSize.HasValue && item.FileSize.Value > 50_000_000)
        {
            try
            {
                DriveInfo drive = new DriveInfo(Path.GetPathRoot(_syncRoot)!);
                if (drive.AvailableFreeSpace < item.FileSize.Value + 50_000_000)
                {
                    _logger.LogWarning("磁盘空间不足，暂停大文件下载: {Path}（需要 {Need}MB，可用 {Avail}MB）",
                        item.FilePath, (item.FileSize.Value + 50_000_000) / 1_048_576, drive.AvailableFreeSpace / 1_048_576);
                    ErrorOccurred?.Invoke(item.FilePath, new ErrorAttribution("磁盘空间不足，已跳过下载", "请清理磁盘空间后重试"), SyncOperation.Download);
                    return true; // 从队列移除，后续由全量扫描重新发现
                }
            }
            catch (Exception ex) { _logger.LogWarning(ex, "获取磁盘信息失败"); }
        }

        NotifyStatus($"下载 ({_queueCompleted + 1}/{_totalFileCount}): {Path.GetFileName(item.FilePath)}");

        var result = await _api.DownloadAsync(item.FilePath, localPath, ct: ct);

        // 1. 下载完成后检查文件是否存在
        if (!File.Exists(localPath))
        {
            item.RetryCount++;
            item.LastError = "下载后文件不存在";
            _logger.LogWarning($"下载后文件不存在（{item.RetryCount}/{MaxRetryCount}）: {item.FilePath}");
            ErrorOccurred?.Invoke(item.FilePath, new ErrorAttribution("下载后文件不存在", "文件可能已在服务端被删除，请刷新后再试"), SyncOperation.Download);
            return false; // 留在队列；RetryCount 递增使 MaxRetryCount 兜底生效（修复无限重试）
        }

        // 2. 如果服务端返回了 X-File-Hash 头，计算本地文件哈希并比对
        if (!string.IsNullOrEmpty(result?.ExpectedHash))
        {
            string actualHash = await FileHasher.ComputeSha256Async(localPath);
            if (!string.Equals(actualHash, result.ExpectedHash, StringComparison.OrdinalIgnoreCase))
            {
                item.RetryCount++;
                item.LastError = "下载后哈希校验失败";
                _logger.LogWarning($"下载后哈希不匹配: {item.FilePath}（期望: {result.ExpectedHash[..16]}..., 实际: {actualHash[..16]}...），重试 {item.RetryCount}/{MaxRetryCount}");
                ErrorOccurred?.Invoke(item.FilePath, new ErrorAttribution("下载后文件校验失败", "文件可能已损坏，请重试；若反复失败请重新同步"), SyncOperation.Download);
                return false;
            }
        }

        // 3. 设置服务端最后修改时间
        if (result?.LastModified != null && DateTime.TryParse(result.LastModified, out var dt))
        {
            File.SetLastWriteTimeUtc(localPath, dt);
        }

        // 下载成功后更新本地快照（延后更新，避免下载失败时幻同步）
        await using var db = await _dbFactory.CreateDbContextAsync();
        var dbSnapshot = await db.RemoteSnapshots.FindAsync(item.FilePath);

        // 获取下载后文件的实际哈希和大小（优先使用服务端返回的 ExpectedHash，避免重复计算）
        string downloadedHash;
        if (!string.IsNullOrEmpty(result?.ExpectedHash))
        {
            downloadedHash = result.ExpectedHash;
        }
        else
        {
            downloadedHash = await FileHasher.ComputeSha256Async(localPath);
        }

        long downloadedSize = new FileInfo(localPath).Length;

        if (dbSnapshot != null)
        {
            dbSnapshot.Version = item.BaseVersion ?? dbSnapshot.Version;
            dbSnapshot.Hash = downloadedHash;
            dbSnapshot.Size = downloadedSize;
            dbSnapshot.State = (int)CloudPan.Contract.FileState.Synced;
            dbSnapshot.LastModified = result?.LastModified; // T-036：快照记录远程真实修改时间
            dbSnapshot.IsDownloaded = true; // T-037：下载完成即本地已落盘，全量扫描可据此判定删除
        }
        else
        {
            // 快照不存在时创建新快照（例如通过 DownloadPathAsync 手动触发的下载）
            db.RemoteSnapshots.Add(new RemoteSnapshot
            {
                Path = item.FilePath,
                Type = (int)CloudPan.Contract.FileType.File,
                Hash = downloadedHash,
                Size = downloadedSize,
                Version = item.BaseVersion ?? 0,
                State = (int)CloudPan.Contract.FileState.Synced,
                LastModified = result?.LastModified,
                IsDownloaded = true // T-037：下载完成即本地已落盘
            });
        }
        await db.SaveChangesAsync();

        _logger.LogInformation($"下载完成: {item.FilePath}");
        return true;
    }

    /// <summary>将下载任务重新入队。</summary>
    private async Task EnqueueDownloadAsync(string filePath, int? baseVersion)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var existing = await db.SyncQueue
            .FirstOrDefaultAsync(q => q.FilePath == filePath && q.Operation == (int)SyncOperation.Download);
        if (existing != null)
        {
            return; // 已在队列中
        }

        db.SyncQueue.Add(new SyncQueue
        {
            FilePath = filePath,
            Operation = (int)SyncOperation.Download,
            Priority = (int)QueuePriority.Normal,
            BaseVersion = baseVersion ?? 0
        });
        await db.SaveChangesAsync();
    }

    /// <returns>true = 成功，应从队列移除</returns>
    private async Task<bool> ProcessDeleteAsync(SyncQueue item, CancellationToken ct)
    {
        // 先调 API 删除服务端，成功后再删本地
        // 如果服务端返回 404（已删除），视为成功继续删本地
        try
        {
            await _api.DeleteAsync(item.FilePath, item.BaseVersion ?? 0, ct);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // 服务端已不存在，继续删除本地即可
        }

        string localPath = ToLocalPath(item.FilePath);
        if (File.Exists(localPath))
        {
            SafeDelete(localPath);
            _logger.LogInformation($"本地删除: {item.FilePath}");
        }

        await using var db = await _dbFactory.CreateDbContextAsync();
        var snapshot = await db.RemoteSnapshots.FindAsync(item.FilePath);
        if (snapshot != null)
        {
            db.RemoteSnapshots.Remove(snapshot);
            await db.SaveChangesAsync();
        }

        return true;
    }

    /// <returns>true = 成功</returns>
    private async Task<bool> ProcessRenameAsync(SyncQueue item, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(item.TargetPath))
        {
            _logger.LogWarning("重命名操作缺少目标路径: {Path}", item.FilePath);
            return true;
        }
        NotifyStatus($"重命名: {item.FilePath} → {item.TargetPath}");
        await _api.MoveAsync(item.FilePath, item.TargetPath, item.BaseVersion ?? 0, ct);
        await using var db = await _dbFactory.CreateDbContextAsync();
        var snapshot = await db.RemoteSnapshots.FindAsync(item.FilePath);
        if (snapshot != null)
        {
            db.RemoteSnapshots.Remove(snapshot);
        }
        // 为新路径创建快照，避免下次全量扫描将新文件视为"新文件"重新上传
        db.RemoteSnapshots.Add(new RemoteSnapshot
        {
            Path = item.TargetPath,
            Type = snapshot?.Type ?? (int)CloudPan.Contract.FileType.File,
            Hash = snapshot?.Hash,
            Size = snapshot?.Size ?? 0,
            Version = item.BaseVersion ?? snapshot?.Version ?? 0,
            State = (int)CloudPan.Contract.FileState.Synced,
            LastModified = snapshot?.LastModified, // T-036：跟随旧快照的远程修改时间
            IsDownloaded = true // T-037：重命名目标已在本机落盘
        });
        await db.SaveChangesAsync();
        _logger.LogInformation("重命名完成: {Old} → {New}", item.FilePath, item.TargetPath);
        return true;
    }

    /// <summary>按需下载指定路径的文件（CloudOnly → 本地）。</summary>
    public async Task DownloadPathAsync(string path, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        db.SyncQueue.Add(new SyncQueue
        {
            FilePath = path,
            Operation = (int)SyncOperation.Download,
            Priority = (int)QueuePriority.High,
            CreatedAt = DateTime.UtcNow.ToString("O")
        });
        await db.SaveChangesAsync();
        _logger.LogInformation("按需下载入队: {Path}", path);
    }

    /// <summary>解析 /api/tree 的 lastModified（ISO 8601）为本地时间；解析失败返回 null（远程版本面板显示未知）。</summary>
    private static DateTime? ParseRemoteLastModified(string? lastModified)
    {
        if (string.IsNullOrEmpty(lastModified))
        {
            return null;
        }
        if (DateTime.TryParse(lastModified, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out DateTime dt))
        {
            return dt.ToLocalTime();
        }
        return null;
    }
}
