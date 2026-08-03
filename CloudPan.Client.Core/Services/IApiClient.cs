using CloudPan.Contract;

namespace CloudPan.Client.Core.Services;

/// <summary>
/// 服务端 API 客户端接口。
/// 提取接口以便 SyncEngine 单元测试时可 mock。
/// </summary>
public interface IApiClient
{
    /// <summary>运行时更新上传限速（T-063，无需重启客户端）。0 = 不限速。</summary>
    void SetUploadLimit(long bytesPerSecond);

    /// <summary>运行时更新下载限速（T-063，无需重启客户端）。0 = 不限速。</summary>
    void SetDownloadLimit(long bytesPerSecond);

    /// <summary>健康检查。</summary>
    Task<bool> HealthCheckAsync(CancellationToken ct = default);

    /// <summary>健康检查（设置页测试连接用，T-053）：失败抛底层异常（HttpRequestException/TaskCanceledException），供调用方按 ErrorAttribution 风格白话归因；成功正常返回。</summary>
    Task EnsureHealthAsync(CancellationToken ct = default);

    /// <summary>获取文件树（增量）。</summary>
    Task<FileTreeResponse?> GetFileTreeAsync(int sinceVersion, int limit = 5000, string? subPath = null, string? cursor = null, CancellationToken ct = default);

    /// <summary>上传文件。返回服务端响应。</summary>
    Task<UploadResponse?> UploadAsync(string localPath, string remotePath, int baseVersion, string lastModified, IProgress<long>? progress = null, CancellationToken ct = default);

    /// <summary>下载文件。返回服务端文件最后修改时间和期望哈希。</summary>
    /// <exception cref="InvalidDataException">文件 SHA-256 与服务端不匹配。</exception>
    Task<DownloadResult?> DownloadAsync(string remotePath, string localPath, IProgress<long>? progress = null, CancellationToken ct = default);

    /// <summary>删除文件。</summary>
    Task DeleteAsync(string path, int baseVersion, CancellationToken ct = default);

    /// <summary>移动/重命名文件。</summary>
    Task MoveAsync(string oldPath, string newPath, int baseVersion, CancellationToken ct = default);

    /// <summary>创建文件夹。</summary>
    Task MkdirAsync(string path, CancellationToken ct = default);

    /// <summary>分块上传文件（自动判断 <10MB 直传、>=10MB 分块）。</summary>
    Task<UploadResponse?> UploadChunkedAsync(
        string localPath, string remotePath, int baseVersion, string lastModified,
        IProgress<long>? progress = null, CancellationToken ct = default);

    /// <summary>查询分块上传进度。</summary>
    Task<ChunkStatusResponse?> GetChunkStatusAsync(string path, string? fileHash = null, CancellationToken ct = default);

    /// <summary>获取回收站列表（按删除时间倒序）。</summary>
    Task<List<TrashItem>> GetTrashAsync(CancellationToken ct = default);

    /// <summary>恢复回收站条目到原位（撤销删除）。</summary>
    Task RestoreTrashAsync(string metaFileName, CancellationToken ct = default);

    /// <summary>清空回收站。</summary>
    Task EmptyTrashAsync(CancellationToken ct = default);

    /// <summary>创建分享链接（/api/shares，T-018）。</summary>
    Task<ShareCreateResponse?> CreateShareAsync(string filePath, string? password, string? expiresAt, int? maxDownloads, CancellationToken ct = default);

    /// <summary>撤销分享链接（DELETE /api/shares/{shareId}，T-018）。返回 false 表示分享不存在或已失效。</summary>
    Task<bool> RevokeShareAsync(string shareId, CancellationToken ct = default);

    /// <summary>获取文件历史版本列表（GET /api/versions，T-018，按版本倒序）。</summary>
    Task<List<VersionItem>> GetVersionsAsync(string path, int limit = 50, CancellationToken ct = default);

    /// <summary>回滚文件到指定历史版本（POST /api/versions/restore，T-018）。</summary>
    Task<VersionRestoreResponse?> RestoreVersionAsync(string filePath, int version, CancellationToken ct = default);
}
