using CloudPan.Shared;

namespace CloudPan.Client.Services;

/// <summary>
/// 服务端 API 客户端接口。
/// 提取接口以便 SyncEngine 单元测试时可 mock。
/// </summary>
public interface IApiClient
{
    /// <summary>健康检查。</summary>
    Task<bool> HealthCheckAsync();

    /// <summary>获取文件树（增量）。</summary>
    Task<FileTreeApiResponse?> GetFileTreeAsync(int sinceVersion, int limit = 5000, string? subPath = null, string? cursor = null);

    /// <summary>上传文件。返回服务端响应。</summary>
    Task<UploadApiResponse?> UploadAsync(string localPath, string remotePath, int baseVersion, string lastModified, IProgress<long>? progress = null);

    /// <summary>下载文件。返回服务端文件最后修改时间。</summary>
    /// <exception cref="InvalidDataException">文件 SHA-256 与服务端不匹配。</exception>
    Task<string?> DownloadAsync(string remotePath, string localPath, IProgress<long>? progress = null);

    /// <summary>删除文件。</summary>
    Task DeleteAsync(string path, int baseVersion);

    /// <summary>移动/重命名文件。</summary>
    Task MoveAsync(string oldPath, string newPath, int baseVersion);

    /// <summary>创建文件夹。</summary>
    Task MkdirAsync(string path);
}
