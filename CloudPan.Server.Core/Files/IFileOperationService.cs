using CloudPan.Infrastructure.Models;

namespace CloudPan.Server.Core;

/// <summary>删除文件结果。DeletedVersion 为删除后分配的新全局版本号。</summary>
public sealed record FileDeleteResult(bool Success, string? Path, int? DeletedVersion, DomainError? Error = null);

/// <summary>移动/重命名结果。</summary>
public sealed record FileMoveResult(bool Success, string? OldPath, string? NewPath, int? Version, DomainError? Error = null);

/// <summary>创建目录结果。</summary>
public sealed record FileMkdirResult(bool Success, string? Path, DomainError? Error = null);

/// <summary>下载文件结果。Success 时 Content 为可读取的文件流。</summary>
public sealed record FileDownloadResult(bool Success, FileEntry? Entry, FileStream? Content, string? FileName, long Size, DomainError? Error = null);

/// <summary>上传冲突处理结果。</summary>
public sealed record UploadConflictResult(string ConflictPath, int CurrentVersion, int BaseVersion);

/// <summary>
/// 文件操作领域服务。封装删除（索引+回收站）、移动（DB 回滚）、建目录、下载、
/// 以及上传冲突副本保存（DB+FS 一致性由本服务保证，F-02 下沉载体）。
/// </summary>
public interface IFileOperationService
{
    /// <summary>删除文件/目录：先删索引、再移入回收站（失败兜底物理删除），写入审计日志。</summary>
    Task<FileDeleteResult> DeleteAsync(string path, int baseVersion, string deviceId);

    /// <summary>移动/重命名：先更新索引、再移动物理文件（失败回滚索引），写入审计日志。</summary>
    Task<FileMoveResult> MoveAsync(string oldPath, string newPath, int baseVersion, string deviceId);

    /// <summary>创建文件夹（物理目录 + 索引），路径已存在返回冲突。</summary>
    Task<FileMkdirResult> MkdirAsync(string path);

    /// <summary>下载文件：校验索引与磁盘存在，打开读取流。</summary>
    Task<FileDownloadResult> DownloadAsync(string path);

    /// <summary>保存上传冲突副本（_冲突_yyyyMMdd_HHmmss 后缀），写入冲突索引与审计日志。</summary>
    Task<UploadConflictResult> HandleUploadConflictAsync(
        string path, Stream content, long length, string? lastModified,
        int baseVersion, int currentVersion, string deviceId);
}
