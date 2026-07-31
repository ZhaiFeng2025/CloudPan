using CloudPan.Server.Models;
using CloudPan.Shared;

namespace CloudPan.Server.Services;

/// <summary>
/// 文件索引服务接口。
/// </summary>
public interface IFileIndexService
{
    Task<FileTreeResponse> GetFileTreeAsync(int? sinceVersion = null, string? subPath = null, int limit = 5000, string? cursor = null);
    Task<FileEntry?> GetByPathAsync(string path);
    Task<FileEntry> UpsertFileAsync(string path, FileType type, string? hash, long size, string lastModified, int newVersion, FileState state = FileState.Synced);
    Task<List<string>> DeleteAsync(string path, bool isDirectory);
    Task MoveAsync(string oldPath, string newPath, int newVersion, bool isDirectory);
    Task CreateDirectoryAsync(string path, int version);
    Task<List<FileEntryDto>> SearchAsync(string query, int limit = 50);
}
