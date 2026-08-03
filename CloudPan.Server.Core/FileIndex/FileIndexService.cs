using CloudPan.Contract;
using CloudPan.Infrastructure.Models;
using CloudPan.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CloudPan.Server.Core;

/// <summary>
/// 文件索引服务。管理 FileEntry 的 CRUD 操作。
/// 版本号由 VersionService 统一分配。
/// </summary>
public class FileIndexService : IFileIndexService
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
        {
            query = query.Where(f => f.Version > sinceVersion.Value);
        }

        // 子目录过滤
        if (!string.IsNullOrEmpty(subPath))
        {
            string prefix = subPath.TrimEnd('/') + "/";
            query = query.Where(f => f.Path.StartsWith(prefix));
        }

        // 游标分页（基于 Path）
        if (!string.IsNullOrEmpty(cursor))
        {
            query = query.Where(f => f.Path.CompareTo(cursor) > 0);
        }

        query = query.OrderBy(f => f.Path).Take(limit + 1);

        var items = await query.ToListAsync();
        bool hasMore = items.Count > limit;
        if (hasMore)
        {
            items.RemoveAt(items.Count - 1);
        }

        int maxVersion = await db.FileEntries.MaxAsync(f => (int?)f.Version) ?? 0;

        return new FileTreeResponse(
            items.Select(MapToDto).ToArray(),
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
    /// 软删除（墓碑机制）：将文件/目录及其子条目标记为 FileState.Deleting 并提升版本号、更新时间戳，
    /// 不物理移除 FileEntry 行（F-05 双向同步删除传播的前提——客户端树查询据 Deleting 状态删除本地副本）。
    /// 物理清理由 PurgeExpiredTombstonesAsync 在保留窗口到期后承担。
    /// </summary>
    public async Task<List<string>> SoftDeleteAsync(string path, bool isDirectory, int newVersion)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        List<string> affectedPaths = new List<string>();
        List<FileEntry> targets = new List<FileEntry>();
        string timestamp = DateTime.UtcNow.ToString("O");

        if (isDirectory)
        {
            // 目录：目录自身条目 + 全部后代。生产目录条目按 T-046 约定无尾斜杠存储（/dir 而非 /dir/），
            // 仅前缀匹配（Path LIKE 'path/%'）会漏掉目录自身条目 → 目录 FileEntry 永不被墓碑化，
            // 空目录在服务端索引中幽灵残留（F-49）。改为 `Path == path OR Path LIKE 'path/%'`：
            //   · Path == normalized 命中目录自身条目（无尾斜杠存储）；
            //   · Path.StartsWith(normalized + "/") 命中全部后代（含历史带尾斜杠的 /dir/ 自身条目）。
            string normalized = path.TrimEnd('/');
            targets.AddRange(await db.FileEntries
                .Where(f => f.Path == normalized || f.Path.StartsWith(normalized + "/"))
                .ToListAsync());
        }
        else
        {
            var entry = await db.FileEntries.FindAsync(path);
            if (entry != null)
            {
                targets.Add(entry);
            }
        }

        foreach (var target in targets)
        {
            target.State = (int)FileState.Deleting;
            target.Version = newVersion;
            target.LastModified = timestamp;
            affectedPaths.Add(target.Path);
        }

        await db.SaveChangesAsync();
        return affectedPaths;
    }

    /// <summary>
    /// 物理清理超过保留窗口的墓碑：删除 FileState.Deleting 且 LastModified 早于 cutoff 的
    /// FileEntry 行及其关联 VersionRecord（FK 约束要求先删版本记录）。
    /// </summary>
    public async Task<int> PurgeExpiredTombstonesAsync(DateTime cutoff)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        string cutoffStr = cutoff.ToString("O");

        var tombstones = await db.FileEntries
            .Where(f => f.State == (int)FileState.Deleting
                && string.Compare(f.LastModified, cutoffStr) < 0)
            .ToListAsync();
        if (tombstones.Count == 0)
        {
            return 0;
        }

        List<string> paths = tombstones.Select(t => t.Path).ToList();
        var versions = await db.VersionRecords
            .Where(v => paths.Contains(v.FilePath))
            .ToListAsync();
        db.VersionRecords.RemoveRange(versions);
        db.FileEntries.RemoveRange(tombstones);
        await db.SaveChangesAsync();
        return tombstones.Count;
    }

    /// <summary>
    /// 移动/重命名文件条目（递归处理子文件）。
    /// 使用 SQLite 原生 UPDATE 直接修改主键（SQLite 允许），避免两步提交的数据丢失风险。
    /// </summary>
    public async Task MoveAsync(string oldPath, string newPath, int newVersion, bool isDirectory)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();

        string timestamp = DateTime.UtcNow.ToString("O");

        try
        {
            // 原子更新主条目路径
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE FileEntry SET Path = {0}, Version = {1}, LastModified = {2} WHERE Path = {3}",
                newPath, newVersion, timestamp, oldPath);

            if (isDirectory)
            {
                string oldPrefix = oldPath.TrimEnd('/') + "/";
                string newPrefix = newPath.TrimEnd('/') + "/";

                // 注意：SQLite 的 REPLACE(Path, oldPrefix, newPrefix) 会替换行内所有匹配段，
                // 嵌套同名目录重命名后路径错乱（/photos/photos/img.jpg 变 /backup/photos/backup/photos/img.jpg）。
                // 改为按前缀长度裁剪的字符串拼接：newPrefix + 原路径前缀之后的剩余部分。
                // SQLite SUBSTR 为 1 基索引，前缀长度 +1 即剩余部分起点。
                await db.Database.ExecuteSqlRawAsync(
                    "UPDATE FileEntry SET Path = {0} || SUBSTR(Path, {1}), Version = {2} WHERE Path LIKE {3}",
                    newPrefix, oldPrefix.Length + 1, newVersion, oldPrefix + "%");
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

        var existing = await db.FileEntries.FindAsync(path);
        if (existing != null)
        {
            // T-049：同名墓碑（FileState.Deleting）复活为有效目录——目录软删后（墓碑保留窗口内）
            // 同名重建不再因『已存在路径』返回 409。墓碑已被物理清理的路径（FileEntry 行已删）
            // 走下方新建分支。非墓碑（Synced 活动条目）路径已存在 → 维持抛异常。
            if (existing.State == (int)FileState.Deleting)
            {
                existing.Type = (int)FileType.Directory;
                existing.CurrentHash = null;
                existing.CurrentSize = 0;
                existing.Version = version;
                existing.LastModified = DateTime.UtcNow.ToString("O");
                existing.State = (int)FileState.Synced;
                await db.SaveChangesAsync();
                return;
            }

            throw new InvalidOperationException($"路径已存在: {path}");
        }

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
        // 排除墓碑（FileState.Deleting）：已删除文件不应出现在用户搜索中
        var items = await db.FileEntries
            .Where(f => f.Path.Contains(query)
                && f.Type == (int)FileType.File
                && f.State != (int)FileState.Deleting)
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
