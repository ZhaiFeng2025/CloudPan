using CloudPan.Shared;
using CloudPan.Server.Data;
using CloudPan.Server.Models;
using Microsoft.EntityFrameworkCore;

namespace CloudPan.Server.Services;

/// <summary>
/// 文件索引服务。管理 FileEntry 的 CRUD 操作。
/// 版本号由 VersionService 统一分配。
/// </summary>
public class FileIndexService
{
    private readonly IDbContextFactory<CloudPanDbContext> _dbFactory;

    public FileIndexService(IDbContextFactory<CloudPanDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    /// <summary>
    /// 获取文件树。支持增量拉取（sinceVersion）和分页（cursor）。
    /// </summary>
    public async Task<FileTreeResponse> GetFileTreeAsync(
        int? sinceVersion = null, string? subPath = null,
        int limit = 5000, string? cursor = null)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        IQueryable<FileEntry> query = db.FileEntries;

        // 增量：仅返回版本号大于 sinceVersion 的文件
        if (sinceVersion.HasValue)
            query = query.Where(f => f.Version > sinceVersion.Value);

        // 子目录过滤
        if (!string.IsNullOrEmpty(subPath))
        {
            var prefix = subPath.TrimEnd('/') + "/";
            query = query.Where(f => f.Path.StartsWith(prefix));
        }

        // 游标分页（基于 Path）
        if (!string.IsNullOrEmpty(cursor))
            query = query.Where(f => f.Path.CompareTo(cursor) > 0);

        query = query.OrderBy(f => f.Path).Take(limit + 1);

        var items = await query.ToListAsync();
        var hasMore = items.Count > limit;
        if (hasMore) items.RemoveAt(items.Count - 1);

        var maxVersion = await db.FileEntries.MaxAsync(f => (int?)f.Version) ?? 0;

        return new FileTreeResponse(
            items.Select(MapToDto).ToList(),
            hasMore ? items.Last().Path : null,
            hasMore,
            maxVersion
        );
    }

    /// <summary>
    /// 按路径查找单个文件条目。
    /// </summary>
    public async Task<FileEntry?> GetByPathAsync(string path)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.FileEntries.FindAsync(path);
    }

    /// <summary>
    /// 创建或更新文件条目。返回新的版本号。
    /// </summary>
    public async Task<FileEntry> UpsertFileAsync(
        string path, FileType type, string? hash, long size,
        string lastModified, int newVersion, FileState state = FileState.Synced)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var existing = await db.FileEntries.FindAsync(path);
        if (existing != null)
        {
            existing.CurrentHash = hash;
            existing.CurrentSize = (int)size;
            existing.Version = newVersion;
            existing.LastModified = lastModified;
            existing.State = (int)state;
        }
        else
        {
            existing = new FileEntry
            {
                Path = path,
                Type = (int)type,
                CurrentHash = hash,
                CurrentSize = (int)size,
                Version = newVersion,
                LastModified = lastModified,
                State = (int)state,
                CreatedAt = DateTime.UtcNow.ToString("O")
            };
            db.FileEntries.Add(existing);
        }

        await db.SaveChangesAsync();
        return existing;
    }

    /// <summary>
    /// 标记文件为删除状态（软删除）。
    /// </summary>
    public async Task MarkDeletedAsync(string path, int newVersion)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var entry = await db.FileEntries.FindAsync(path);
        if (entry != null)
        {
            entry.State = (int)FileState.Deleting;
            entry.Version = newVersion;
            await db.SaveChangesAsync();
        }
    }

    /// <summary>
    /// 物理删除文件条目及其子文件（递归删除文件夹）。
    /// </summary>
    public async Task<List<string>> DeleteAsync(string path, bool isDirectory)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var deletedPaths = new List<string>();

        if (isDirectory)
        {
            var prefix = path.TrimEnd('/') + "/";
            var children = await db.FileEntries
                .Where(f => f.Path.StartsWith(prefix))
                .ToListAsync();

            foreach (var child in children)
            {
                deletedPaths.Add(child.Path);

                // 记录版本
                db.VersionRecords.Add(new VersionRecord
                {
                    FilePath = child.Path,
                    Version = child.Version,
                    Hash = child.CurrentHash ?? "",
                    Size = child.CurrentSize,
                    StoragePath = "",
                    Timestamp = DateTime.UtcNow.ToString("O"),
                    DeviceId = "server",
                    RestoredFromVersion = null
                });
            }

            db.FileEntries.RemoveRange(children);
        }

        var entry = await db.FileEntries.FindAsync(path);
        if (entry != null)
        {
            deletedPaths.Add(entry.Path);
            db.FileEntries.Remove(entry);
        }

        await db.SaveChangesAsync();
        return deletedPaths;
    }

    /// <summary>
    /// 移动/重命名文件条目（递归处理子文件）。
    /// </summary>
    public async Task MoveAsync(string oldPath, string newPath, int newVersion, bool isDirectory)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var entry = await db.FileEntries.FindAsync(oldPath);
        if (entry == null) throw new KeyNotFoundException($"文件不存在: {oldPath}");

        // 更新主条目
        entry.Path = newPath;
        entry.Version = newVersion;
        entry.LastModified = DateTime.UtcNow.ToString("O");

        // 如果是目录，递归更新所有子文件路径
        if (isDirectory)
        {
            var oldPrefix = oldPath.TrimEnd('/') + "/";
            var newPrefix = newPath.TrimEnd('/') + "/";
            var children = await db.FileEntries
                .Where(f => f.Path.StartsWith(oldPrefix))
                .ToListAsync();

            foreach (var child in children)
            {
                child.Path = newPrefix + child.Path[oldPrefix.Length..];
                child.Version = newVersion;
            }
        }

        await db.SaveChangesAsync();
    }

    /// <summary>
    /// 创建文件夹条目。
    /// </summary>
    public async Task CreateDirectoryAsync(string path)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        if (await db.FileEntries.AnyAsync(f => f.Path == path))
            throw new InvalidOperationException($"路径已存在: {path}");

        db.FileEntries.Add(new FileEntry
        {
            Path = path,
            Type = (int)FileType.Directory,
            CurrentHash = null,
            CurrentSize = 0,
            Version = 0,
            LastModified = DateTime.UtcNow.ToString("O"),
            State = (int)FileState.Synced,
            CreatedAt = DateTime.UtcNow.ToString("O")
        });

        await db.SaveChangesAsync();
    }

    /// <summary>
    /// 搜索文件（按文件名 LIKE 匹配）。
    /// </summary>
    public async Task<List<FileEntryDto>> SearchAsync(string query, int limit = 50)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var items = await db.FileEntries
            .Where(f => f.Path.Contains(query) && f.Type == (int)FileType.File)
            .Take(limit)
            .ToListAsync();

        return items.Select(MapToDto).ToList();
    }

    private static FileEntryDto MapToDto(FileEntry f)
    {
        return new FileEntryDto(
            f.Path,
            f.Type,
            f.CurrentHash,
            f.CurrentSize,
            f.Version,
            f.LastModified,
            f.State
        );
    }
}

/// <summary>
/// 文件树 API 响应。
/// </summary>
public record FileTreeResponse(
    List<FileEntryDto> Data,
    string? NextCursor,
    bool HasMore,
    int MaxVersion
);
