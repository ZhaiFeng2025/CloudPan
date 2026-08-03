using CloudPan.Contract;
using CloudPan.Infrastructure.Models;
using CloudPan.Infrastructure.Persistence;
using CloudPan.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CloudPan.Server.Core;

/// <summary>『提交新版本』目标状态：新版本 FileEntry 应更新到的值。</summary>
public sealed record VersionCommitState(
    string Path,
    string Hash,
    long Size,
    int NewVersion,
    string LastModified);

/// <summary>
/// 『提交新版本』领域辅助：单点守护『存档旧版本 → 添加 VersionRecord → 裁剪超 MaxVersionsDefault → upsert FileEntry』
/// 的 DB+FS 一致性语义（CLAUDE.md 7.1 高危区）。事务边界、回滚与孤儿存档清理只在此实现，
/// Upload / ChunkedUpload / Restore 三处共用——任一修订不再多处同步。
/// </summary>
public sealed class VersionCommitHelper
{
    private readonly IFileStorageService _storage;
    private readonly ILogger<VersionCommitHelper> _logger;

    public VersionCommitHelper(IFileStorageService storage, ILogger<VersionCommitHelper> logger)
    {
        _storage = storage;
        _logger = logger;
    }

    /// <summary>
    /// 存档旧版本内容（FS）。返回存档 StoragePath（.versions/ 下的文件名）；无旧内容（新建文件）返回 null。
    /// 必须在覆盖目标文件之前调用——否则 StoreVersionAsync 读到的是新内容（F-01 顺序缺陷根因）。
    /// </summary>
    public async Task<string?> ArchiveOldVersionAsync(string path, FileEntry? oldEntry, CancellationToken ct = default)
    {
        if (oldEntry == null || oldEntry.CurrentHash == null)
        {
            return null;
        }
        return await _storage.StoreVersionAsync(path, oldEntry.Version, ct);
    }

    /// <summary>
    /// 单点『提交新版本』事务模板：统一『存档记录 + 裁剪 + upsert FileEntry』的事务边界与回滚。
    /// 调用方须先完成 FS 准备（ArchiveOldVersionAsync 存档旧版本、新内容已就绪/已落盘），
    /// 可通过 extraDbWork 在事务内追加自身 DB 变更（如审计日志、移除分块会话、回滚来源记录）。
    /// 任一 DB 步骤失败：回滚事务并清理孤儿存档文件（FS 副作用，CLAUDE.md 7.3）。
    /// </summary>
    public async Task CommitNewVersionInTransactionAsync(
        CloudPanDbContext db,
        string path,
        FileEntry? archivedEntry,
        string? archiveStoragePath,
        VersionCommitState newState,
        string deviceId,
        bool prune,
        Action? extraDbWork = null,
        CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // 被裁剪版本对应的存档物理文件，事务提交成功后统一删除（FS 副作用，CLAUDE.md 7.3）
        List<string> prunedArchivePaths = new();
        try
        {
            if (archiveStoragePath != null && archivedEntry != null)
            {
                // 存档记录：存档内容 = 覆盖目标前读到的旧内容（存档已完成，见 ArchiveOldVersionAsync）
                db.VersionRecords.Add(new VersionRecord
                {
                    FilePath = path,
                    Version = archivedEntry.Version,
                    Hash = archivedEntry.CurrentHash!,
                    Size = archivedEntry.CurrentSize,
                    StoragePath = archiveStoragePath,
                    Timestamp = DateTime.UtcNow.ToString("O"),
                    DeviceId = deviceId
                });

                if (prune)
                {
                    // 保留最近 N 个版本（N 单源：shared-spec.json → SpecConfig.MaxVersionsDefault）
                    var oldVersions = await db.VersionRecords
                        .Where(v => v.FilePath == path)
                        .OrderByDescending(v => v.Version)
                        .Skip(SpecConfig.MaxVersionsDefault)
                        .ToListAsync(ct);
                    db.VersionRecords.RemoveRange(oldVersions);
                    // 记录被裁剪版本的存档物理文件（提交成功后删除——回滚时其版本记录仍在引用）
                    prunedArchivePaths.AddRange(oldVersions.Select(v => v.StoragePath));
                }
            }

            // upsert FileEntry（同一 DbContext，避免跨上下文游离于事务外）
            var entry = await db.FileEntries.FindAsync(new object?[] { path }, ct);
            if (entry != null)
            {
                entry.CurrentHash = newState.Hash;
                entry.CurrentSize = newState.Size;
                entry.Version = newState.NewVersion;
                entry.LastModified = newState.LastModified;
                entry.State = (int)FileState.Synced;
            }
            else
            {
                db.FileEntries.Add(new FileEntry
                {
                    Path = newState.Path,
                    Type = (int)FileType.File,
                    CurrentHash = newState.Hash,
                    CurrentSize = newState.Size,
                    Version = newState.NewVersion,
                    LastModified = newState.LastModified,
                    State = (int)FileState.Synced,
                    CreatedAt = DateTime.UtcNow.ToString("O")
                });
            }

            extraDbWork?.Invoke();

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync();
            DeleteOrphanArchive(archiveStoragePath);
            throw;
        }

        // 事务提交成功后清理被裁剪版本的存档物理文件（DB 已提交、记录已删，文件不再被引用）。
        // 失败的文件成为孤儿存档，由统一存储回收任务（PurgeOrphanVersionArchivesAsync）兜底。
        foreach (string prunedPath in prunedArchivePaths)
        {
            DeleteOrphanArchive(prunedPath);
        }
    }

    /// <summary>
    /// 『提交新版本』后 FS 原子覆盖失败的索引回滚（CLAUDE.md 7.1 DB+FS 一致性）。
    /// 调用方已完成 <see cref="CommitNewVersionInTransactionAsync"/>（DB 事务已提交、索引指向新 hash/version），
    /// 但随后 Move 覆盖目标失败、磁盘仍是旧内容——若不回滚，客户端下轮树同步对齐错误索引
    /// （本地内容匹配索引 hash）永不重传，索引与磁盘永久不一致（F-21 毒化状态）。
    /// 回滚使索引回到与磁盘一致的旧状态：FileEntry 恢复旧 hash/version/size/LastModified/State
    /// （新建文件则删除条目）、移除本次新增的孤儿 VersionRecord（旧内容仍是磁盘真值）并删除存档文件；
    /// 客户端重试/下轮扫描按哈希差异重新上传收敛。
    /// dbFactory 由调用方传入（不注入构造函数，避免扩散构造签名）；extraRollbackWork 供调用方在事务内
    /// 追加自身版本记录移除（如 RestoreAsync 的 RestoredFromVersion 回滚记录）。
    /// 使用全新 DbContext：不可复用已提交的 db——其变更追踪器仍持有新值而非 DB 真值（CLAUDE.md 7.3）。
    /// </summary>
    public async Task RollbackCommittedVersionAsync(
        IDbContextFactory<CloudPanDbContext> dbFactory,
        string path,
        FileEntry? oldEntry,
        string? archivePath,
        Action<CloudPanDbContext>? extraRollbackWork = null)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        try
        {
            var entry = await db.FileEntries.FindAsync(path);
            if (entry != null)
            {
                if (oldEntry != null)
                {
                    entry.CurrentHash = oldEntry.CurrentHash;
                    entry.CurrentSize = oldEntry.CurrentSize;
                    entry.Version = oldEntry.Version;
                    entry.LastModified = oldEntry.LastModified;
                    entry.State = oldEntry.State;
                }
                else
                {
                    // 新建文件：磁盘上目标从未落位，移除索引条目
                    db.FileEntries.Remove(entry);
                }
            }

            // 移除本次新增的孤儿版本记录（新建文件无存档，archivePath 必为 null，此处不冲突）
            if (archivePath != null)
            {
                var orphan = await db.VersionRecords
                    .FirstOrDefaultAsync(v => v.FilePath == path && v.StoragePath == archivePath);
                if (orphan != null)
                {
                    db.VersionRecords.Remove(orphan);
                }
            }

            extraRollbackWork?.Invoke(db);

            await db.SaveChangesAsync();
            await tx.CommitAsync();

            // 事务提交后清理孤儿存档文件（FS 副作用，CLAUDE.md 7.3，统一经辅助）
            DeleteOrphanArchive(archivePath);
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    /// <summary>回滚/失败后清理孤儿存档文件（FS 副作用，CLAUDE.md 7.3）。尽力而为，幂等。</summary>
    public void DeleteOrphanArchive(string? archiveStoragePath)
    {
        try
        {
            // 孤儿存档清理单点收敛于 IFileStorageService.DeleteVersionArchive（持有 .versions 目录）
            _storage.DeleteVersionArchive(archiveStoragePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "清理孤儿版本存档失败: {Path}", archiveStoragePath);
        }
    }
}
