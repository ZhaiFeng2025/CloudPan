using CloudPan.Client.Core.Services;
using CloudPan.Contract;

namespace CloudPan.Tests.Client.Services;

/// <summary>
/// IApiClient 的 mock 实现——在内存中维护文件列表，模拟服务端行为。
/// 用于 SyncEngine 单元测试。
/// </summary>
public class MockApiClient : IApiClient
{
    /// <summary>内存中的服务端文件列表。</summary>
    public Dictionary<string, (string Hash, long Size, int Version)> Files { get; } = new();

    /// <summary>上传调用记录（路径 → 调用次数）。</summary>
    public Dictionary<string, int> UploadCalls { get; } = new();

    /// <summary>下载调用记录。</summary>
    public Dictionary<string, int> DownloadCalls { get; } = new();

    /// <summary>删除调用记录。</summary>
    public Dictionary<string, int> DeleteCalls { get; } = new();

    /// <summary>回收站条目（模拟服务端 /api/trash 列表）。</summary>
    public List<TrashItem> TrashItems { get; } = new();

    /// <summary>已创建的分享链接（模拟服务端 /api/shares 记录，Key=shareId）。</summary>
    public Dictionary<string, ShareCreateData> Shares { get; } = new();

    /// <summary>历史版本记录（模拟服务端 /api/versions，Key=文件路径）。</summary>
    public Dictionary<string, List<VersionItem>> VersionHistory { get; } = new();

    public bool HealthOk { get; set; } = true;

    /// <summary>模拟认证失败模式（F-34/T-034）：true 时上传一律返回 401，用于测试连续 401 触发重配引导。</summary>
    public bool AuthFailMode { get; set; }

    public Task<bool> HealthCheckAsync(CancellationToken ct = default) => Task.FromResult(HealthOk);

    public Task<FileTreeResponse?> GetFileTreeAsync(int sinceVersion, int limit = 5000, string? subPath = null, string? cursor = null, CancellationToken ct = default)
    {
        List<FileEntryDto> items = Files
            .Where(kv => kv.Value.Version > sinceVersion)
            .Select(kv => new FileEntryDto(
                kv.Key,
                kv.Key.EndsWith('/') ? 1 : 0,
                kv.Value.Hash,
                kv.Value.Size,
                kv.Value.Version,
                DateTime.UtcNow.ToString("O"),
                0 // Synced
            ))
            .ToList();

        return Task.FromResult<FileTreeResponse?>(new FileTreeResponse(
            items.ToArray(),
            null,
            false,
            items.Count > 0 ? items.Max(i => i.Version) : 0));
    }

    public Task<UploadResponse?> UploadAsync(string localPath, string remotePath, int baseVersion, string lastModified, IProgress<long>? progress = null, CancellationToken ct = default)
    {
        UploadCalls.TryGetValue(remotePath, out int count);
        UploadCalls[remotePath] = count + 1;

        // 模拟认证失败（F-34/T-034）：持续 401 → 客户端应触发重配引导
        if (AuthFailMode)
        {
            throw new HttpRequestException("Token 无效（模拟 401）", null, System.Net.HttpStatusCode.Unauthorized);
        }

        // 模拟服务端冲突检测：baseVersion > 0 且服务端当前版本 > baseVersion → 409
        // （对齐 FilesController.Upload / ChunkedUploadService.FinalizeAsync 语义，供并发编辑冲突测试使用）
        if (baseVersion > 0 && Files.TryGetValue(remotePath, out var current) && current.Version > baseVersion)
        {
            throw new HttpRequestException(
                $"版本冲突：客户端基于 v{baseVersion}，服务端当前 v{current.Version}",
                null,
                System.Net.HttpStatusCode.Conflict);
        }

        long size = File.Exists(localPath) ? new FileInfo(localPath).Length : 0;
        int version = Files.Count + 1;

        Files[remotePath] = ("mock-hash", size, version);

        return Task.FromResult<UploadResponse?>(new UploadResponse(
            new UploadData(remotePath, version, "mock-hash", size, false)));
    }

    public async Task<DownloadResult?> DownloadAsync(string remotePath, string localPath, IProgress<long>? progress = null, CancellationToken ct = default)
    {
        DownloadCalls.TryGetValue(remotePath, out int count);
        DownloadCalls[remotePath] = count + 1;

        // 模拟创建文件
        string? dir = Path.GetDirectoryName(localPath);
        if (dir != null)
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(localPath, "mock-content");

        // 返回真实哈希，使下载后校验（X-File-Hash）通过——否则 ProcessDownloadAsync 恒失败
        string actualHash = await FileHasher.ComputeSha256Async(localPath, ct);
        return new DownloadResult
        {
            LastModified = DateTime.UtcNow.ToString("O"),
            ExpectedHash = actualHash
        };
    }

    public async Task DeleteAsync(string path, int baseVersion, CancellationToken ct = default)
    {
        // 模拟 HTTP 404 行为（在测试中通过异常控制）
        DeleteCalls.TryGetValue(path, out int count);
        DeleteCalls[path] = count + 1;
        // 服务端删除 → 移入回收站（对齐 FileOperationService.DeleteAsync 行为，供回收站/撤销测试使用）
        // 目录在 Files 中以路径+"/" 为键（MkdirAsync），客户端传参为无尾斜杠路径
        if (Files.TryGetValue(path, out var info))
        {
            Files.Remove(path);
            TrashItems.Add(new TrashItem(path, "mock_" + Guid.NewGuid().ToString("N")[..8],
                info.Size, false, DateTime.UtcNow.ToString("O"), 0));
        }
        else if (Files.TryGetValue(path + "/", out var dirInfo))
        {
            Files.Remove(path + "/");
            TrashItems.Add(new TrashItem(path, "mock_" + Guid.NewGuid().ToString("N")[..8],
                dirInfo.Size, true, DateTime.UtcNow.ToString("O"), 0));
        }
        await Task.CompletedTask;
    }

    public Task MoveAsync(string oldPath, string newPath, int baseVersion, CancellationToken ct = default)
    {
        if (Files.TryGetValue(oldPath, out var info))
        {
            Files.Remove(oldPath);
            Files[newPath] = info;
        }
        return Task.CompletedTask;
    }

    public Task MkdirAsync(string path, CancellationToken ct = default)
    {
        string dirPath = path.EndsWith('/') ? path : path + "/";
        Files[dirPath] = (null!, 0, Files.Count + 1);
        return Task.CompletedTask;
    }

    /// <summary>分块上传（Mock：<10MB 走直传，>=10MB 模拟分块）。</summary>
    public Task<UploadResponse?> UploadChunkedAsync(
        string localPath, string remotePath, int baseVersion, string lastModified,
        IProgress<long>? progress = null, CancellationToken ct = default)
    {
        // Mock 简化：无论大小，均走 UploadAsync（测试不需要真实分块逻辑）
        return UploadAsync(localPath, remotePath, baseVersion, lastModified, progress);
    }

    /// <summary>查询分块进度（Mock：始终返回 null，表示无进行中上传）。</summary>
    public Task<ChunkStatusResponse?> GetChunkStatusAsync(string path, CancellationToken ct = default)
    {
        return Task.FromResult<ChunkStatusResponse?>(null);
    }

    /// <summary>获取回收站列表（模拟服务端）。</summary>
    public Task<List<TrashItem>> GetTrashAsync(CancellationToken ct = default)
    {
        return Task.FromResult(TrashItems.ToList());
    }

    /// <summary>恢复回收站条目（模拟服务端：移回原位并重建索引）。</summary>
    public Task RestoreTrashAsync(string metaFileName, CancellationToken ct = default)
    {
        var item = TrashItems.FirstOrDefault(t => (t.TrashFileName + ".json") == metaFileName);
        if (item != null)
        {
            TrashItems.Remove(item);
            Files[item.OriginalPath] = ("mock-hash", item.FileSize, Files.Count + 1);
        }
        return Task.CompletedTask;
    }

    /// <summary>清空回收站（模拟服务端）。</summary>
    public Task EmptyTrashAsync(CancellationToken ct = default)
    {
        TrashItems.Clear();
        return Task.CompletedTask;
    }

    /// <summary>创建分享链接（模拟服务端 /api/shares）。</summary>
    public Task<ShareCreateResponse?> CreateShareAsync(
        string filePath, string? password, string? expiresAt, int? maxDownloads, CancellationToken ct = default)
    {
        // 模拟服务端：文件不存在时返回 NOT_FOUND（对齐 SharingService.CreateShareAsync 语义）
        if (!Files.ContainsKey(filePath) && !Files.ContainsKey(filePath + "/"))
        {
            throw new HttpRequestException("文件不存在", null, System.Net.HttpStatusCode.NotFound);
        }

        string shareId = Guid.NewGuid().ToString("N")[..16];
        var data = new ShareCreateData(
            shareId,
            $"http://localhost:8443/share/{shareId}",
            expiresAt,
            maxDownloads);
        Shares[shareId] = data;
        return Task.FromResult<ShareCreateResponse?>(new ShareCreateResponse(data));
    }

    /// <summary>撤销分享链接（模拟服务端 DELETE /api/shares/{shareId}）。</summary>
    public Task<bool> RevokeShareAsync(string shareId, CancellationToken ct = default)
    {
        return Task.FromResult(Shares.Remove(shareId));
    }

    /// <summary>获取文件历史版本列表（模拟服务端 /api/versions）。</summary>
    public Task<List<VersionItem>> GetVersionsAsync(string path, int limit = 50, CancellationToken ct = default)
    {
        return Task.FromResult(VersionHistory.TryGetValue(path, out var list)
            ? list.Take(limit).ToList()
            : new List<VersionItem>());
    }

    /// <summary>回滚到指定历史版本（模拟服务端 /api/versions/restore）。</summary>
    public Task<VersionRestoreResponse?> RestoreVersionAsync(string filePath, int version, CancellationToken ct = default)
    {
        if (!VersionHistory.TryGetValue(filePath, out var list))
        {
            return Task.FromResult<VersionRestoreResponse?>(null);
        }

        var target = list.FirstOrDefault(v => v.Version == version);
        if (target == null)
        {
            return Task.FromResult<VersionRestoreResponse?>(null);
        }

        // 模拟服务端回滚：以目标版本内容更新当前文件（列表最前一条）并提升版本号
        int newVersion = (list.Count > 0 ? list.Max(v => v.Version) : 0) + 1;
        list.Insert(0, new VersionItem(newVersion, target.Hash, target.Size,
            DateTime.UtcNow.ToString("O"), "mock-device", version));
        return Task.FromResult<VersionRestoreResponse?>(new VersionRestoreResponse(
            new VersionRestoreData(filePath, newVersion, target.Hash, target.Size, version)));
    }

    /// <summary>重置所有 mock 状态。</summary>
    public void Reset()
    {
        Files.Clear();
        UploadCalls.Clear();
        DownloadCalls.Clear();
        DeleteCalls.Clear();
        TrashItems.Clear();
        Shares.Clear();
        VersionHistory.Clear();
        HealthOk = true;
        AuthFailMode = false;
    }
}
