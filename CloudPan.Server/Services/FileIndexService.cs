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
            existing.CurrentSize = size;
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
                CurrentSize = size,
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
    /// <summary>
    /// 物理删除文件条目及其子文件（递归删除文件夹）。
    /// 先删除关联的 VersionRecord（满足 FK 约束），再删除 FileEntry。
    /// </summary>
    public async Task<List<string>> DeleteAsync(string path, bool isDirectory)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var deletedPaths = new List<string>();

        // 收集所有待删除路径
        var pathsToDelete = new List<string>();

        if (isDirectory)
        {
            var prefix = path.TrimEnd('/') + "/";
            var children = await db.FileEntries
                .Where(f => f.Path.StartsWith(prefix))
                .ToListAsync();
            pathsToDelete.AddRange(children.Select(c => c.Path));
        }

        var entry = await db.FileEntries.FindAsync(path);
        if (entry != null)
            pathsToDelete.Add(entry.Path);

        // 先删除关联的版本历史（FK 约束要求）
        if (pathsToDelete.Count > 0)
        {
            var versions = await db.VersionRecords
                .Where(v => pathsToDelete.Contains(v.FilePath))
                .ToListAsync();
            db.VersionRecords.RemoveRange(versions);
        }

        // 再删除文件条目
        if (isDirectory)
        {
            var prefix = path.TrimEnd('/') + "/";
            var children = await db.FileEntries
                .Where(f => f.Path.StartsWith(prefix))
                .ToListAsync();
            db.FileEntries.RemoveRange(children);
        }

        if (entry != null)
            db.FileEntries.Remove(entry);

        await db.SaveChangesAsync();
        deletedPaths.AddRange(pathsToDelete);
        return deletedPaths;
    }

    /// <summary>
    /// 移动/重命名文件条目（递归处理子文件）。
    /// 使用 SQLite 原生 UPDATE 直接修改主键（SQLite 允许），避免两步提交的数据丢失风险。
    /// </summary>
    public async Task MoveAsync(string oldPath, string newPath, int newVersion, bool isDirectory)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();

        var timestamp = DateTime.UtcNow.ToString("O");

        try
        {
            // 原子更新主条目路径
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE FileEntry SET Path = {0}, Version = {1}, LastModified = {2} WHERE Path = {3}",
                newPath, newVersion, timestamp, oldPath);

            if (isDirectory)
            {
                var oldPrefix = oldPath.TrimEnd('/') + "/";
                var newPrefix = newPath.TrimEnd('/') + "/";

                // SQLite 的 REPLACE 函数做前缀替换
                await db.Database.ExecuteSqlRawAsync(
                    "UPDATE FileEntry SET Path = REPLACE(Path, {0}, {1}), Version = {2} WHERE Path LIKE {3}",
                    oldPrefix, newPrefix, newVersion, oldPrefix + "%");
            }

            await tx.CommitAsync();
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    /// <summary>
    /// 创建文件夹条目。
    /// </summary>
    public async Task CreateDirectoryAsync(string path, int version)
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
            Version = version,
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
