using CloudPan.Shared;
using CloudPan.Client.Services;

namespace CloudPan.Tests.Client.Services;

/// <summary>
/// IApiClient 的 mock 实现——在内存中维护文件列表，模拟服务端行为。
/// 用于 SyncEngine 单元测试。
/// </summary>
public class MockApiClient : IApiClient
{
    /// <summary>内存中的服务端文件列表。</summary>
    public Dictionary<string, (string Hash, int Size, int Version)> Files { get; } = new();

    /// <summary>上传调用记录（路径 → 调用次数）。</summary>
    public Dictionary<string, int> UploadCalls { get; } = new();

    /// <summary>下载调用记录。</summary>
    public Dictionary<string, int> DownloadCalls { get; } = new();

    /// <summary>删除调用记录。</summary>
    public Dictionary<string, int> DeleteCalls { get; } = new();

    public bool HealthOk { get; set; } = true;

    public Task<bool> HealthCheckAsync() => Task.FromResult(HealthOk);

    public Task<FileTreeApiResponse?> GetFileTreeAsync(int sinceVersion, int limit = 5000, string? subPath = null, string? cursor = null)
    {
        var items = Files
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

        return Task.FromResult<FileTreeApiResponse?>(new FileTreeApiResponse
        {
            Data = items,
            HasMore = false,
            NextCursor = null,
            MaxVersion = items.Count > 0 ? items.Max(i => i.Version) : 0
        });
    }

    public Task<UploadApiResponse?> UploadAsync(string localPath, string remotePath, int baseVersion, string lastModified, IProgress<long>? progress = null)
    {
        UploadCalls.TryGetValue(remotePath, out var count);
        UploadCalls[remotePath] = count + 1;

        var size = File.Exists(localPath) ? (int)new FileInfo(localPath).Length : 0;
        var version = Files.Count + 1;

        Files[remotePath] = ("mock-hash", size, version);

        return Task.FromResult<UploadApiResponse?>(new UploadApiResponse
        {
            Data = new UploadDataDto
            {
                Path = remotePath,
                Version = version,
                Hash = "mock-hash",
                Size = size,
                ConflictResolved = false
            }
        });
    }

    public Task<string?> DownloadAsync(string remotePath, string localPath, IProgress<long>? progress = null)
    {
        DownloadCalls.TryGetValue(remotePath, out var count);
        DownloadCalls[remotePath] = count + 1;

        // 模拟创建文件
        var dir = Path.GetDirectoryName(localPath);
        if (dir != null) Directory.CreateDirectory(dir);
        File.WriteAllText(localPath, "mock-content");

        return Task.FromResult<string?>(DateTime.UtcNow.ToString("O"));
    }

    public async Task DeleteAsync(string path, int baseVersion)
    {
        // 模拟 HTTP 404 行为（在测试中通过异常控制）
        DeleteCalls.TryGetValue(path, out var count);
        DeleteCalls[path] = count + 1;
        Files.Remove(path);
        await Task.CompletedTask;
    }

    public Task MoveAsync(string oldPath, string newPath, int baseVersion)
    {
        if (Files.TryGetValue(oldPath, out var info))
        {
            Files.Remove(oldPath);
            Files[newPath] = info;
        }
        return Task.CompletedTask;
    }

    public Task MkdirAsync(string path)
    {
        var dirPath = path.EndsWith('/') ? path : path + "/";
        Files[dirPath] = (null!, 0, Files.Count + 1);
        return Task.CompletedTask;
    }

    /// <summary>重置所有 mock 状态。</summary>
    public void Reset()
    {
        Files.Clear();
        UploadCalls.Clear();
        DownloadCalls.Clear();
        DeleteCalls.Clear();
        HealthOk = true;
    }
}
