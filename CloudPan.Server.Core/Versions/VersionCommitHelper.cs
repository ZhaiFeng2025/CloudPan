using CloudPan.Contract;
using CloudPan.Infrastructure.Models;
using CloudPan.Infrastructure.Persistence;
using CloudPan.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using IOFile = System.IO.File;

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
    }

    /// <summary>回滚/失败后清理孤儿存档文件（FS 副作用，CLAUDE.md 7.3）。尽力而为，幂等。</summary>
    public void DeleteOrphanArchive(string? archiveStoragePath)
    {
        if (archiveStoragePath == null)
        {
            return;
        }
        try
        {
            string archiveFile = Path.Combine(
                _storage.GetAbsolutePath("/"), ".cloudpan", ".versions", archiveStoragePath);
            if (IOFile.Exists(archiveFile))
            {
                IOFile.Delete(archiveFile);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "清理孤儿版本存档失败: {Path}", archiveStoragePath);
        }
    }
}
