using CloudPan.Contract;
using CloudPan.Infrastructure.Models;

namespace CloudPan.Server.Core;

/// <summary>
/// 文件索引服务接口。
/// </summary>
public interface IFileIndexService
{
    Task<FileTreeResponse> GetFileTreeAsync(int? sinceVersion = null, string? subPath = null, int limit = 5000, string? cursor = null);
    Task<FileEntry?> GetByPathAsync(string path);
    Task<FileEntry> UpsertFileAsync(string path, FileType type, string? hash, long size, string lastModified, int newVersion, FileState state = FileState.Synced);

    /// <summary>软删除（墓碑）：将文件/目录及其子条目标记为 FileState.Deleting 并提升版本号，不物理移除。</summary>
    Task<List<string>> SoftDeleteAsync(string path, bool isDirectory, int newVersion);

    /// <summary>物理清理超过保留窗口的墓碑（FileState.Deleting 且 LastModified 早于 cutoff）。返回清理条数。</summary>
    Task<int> PurgeExpiredTombstonesAsync(DateTime cutoff);

    /// <summary>清理 .versions 目录中未被任何 VersionRecord 引用的孤儿存档物理文件（统一存储回收兜底）。返回清理文件数。</summary>
    Task<int> PurgeOrphanVersionArchivesAsync();

    Task MoveAsync(string oldPath, string newPath, int newVersion, bool isDirectory);
    Task CreateDirectoryAsync(string path, int version);
    Task<List<FileEntryDto>> SearchAsync(string query, int limit = 50);
}
