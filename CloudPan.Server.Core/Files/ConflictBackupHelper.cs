using CloudPan.Contract;
using CloudPan.Infrastructure.Models;
using CloudPan.Infrastructure.Storage;

namespace CloudPan.Server.Core;

/// <summary>『保存冲突副本』结果：冲突副本路径 + 服务端当前版本 + 客户端 baseVersion。</summary>
public sealed record ConflictSaveResult(string ConflictPath, int CurrentVersion, int BaseVersion);

/// <summary>
/// 『保存冲突副本』领域辅助：单点统一『冲突检测 → 冲突路径拼接 → 版本分配 → 原子写副本 →
/// UpsertFileAsync(FileState.Conflict) → 审计日志』（CLAUDE.md 7.1 DB+FS 一致性高危区）。
/// Upload（FileOperationService.HandleUploadConflictAsync）与分块上传（ChunkedUploadService.FinalizeAsync）
/// 两处共用——消除 AtomicWrite vs IOFile.Copy 分叉，任一修订不再多处同步，命名/原子性/状态标记不漂移。
/// </summary>
public sealed class ConflictBackupHelper
{
    private readonly IFileStorageService _storage;
    private readonly IFileIndexService _index;
    private readonly IVersionService _version;
    private readonly ISyncLogService _syncLog;

    public ConflictBackupHelper(
        IFileStorageService storage,
        IFileIndexService index,
        IVersionService version,
        ISyncLogService syncLog)
    {
        _storage = storage;
        _index = index;
        _version = version;
        _syncLog = syncLog;
    }

    /// <summary>
    /// 冲突检测 + 保存冲突副本。服务端当前版本未超过客户端 baseVersion（或 baseVersion ≤ 0）视为未冲突，
    /// 返回 null（不分配版本、不写副本、不写审计日志）。冲突时：分配新版本 → 拼冲突路径 → 原子写副本 →
    /// upsert FileState.Conflict 索引 → 审计日志。
    /// </summary>
    public async Task<ConflictSaveResult?> SaveConflictCopyIfNeededAsync(
        string path, int baseVersion, int currentVersion,
        Stream content, long length, string? lastModified, string deviceId)
    {
        // 冲突检测（单点统一：baseVersion > 0 且服务端当前版本更高）
        if (baseVersion <= 0 || currentVersion <= baseVersion)
        {
            return null;
        }

        int conflictVersion = await _version.NextVersionAsync();

        // 冲突路径拼接（单点统一）：/目录/名_冲突_yyyyMMdd_HHmmss.ext
        string nameWithoutExt = Path.GetFileNameWithoutExtension(path);
        string ext = Path.GetExtension(path);
        string suffix = DateTime.Now.ToString(SpecConfig.ConflictSuffixPattern); // 单源：shared-spec.json → SpecConfig.ConflictSuffixPattern
        string conflictPath = Path.GetDirectoryName(path)?.Replace('\\', '/') ?? "";
        if (!conflictPath.EndsWith('/') && !string.IsNullOrEmpty(conflictPath))
        {
            conflictPath += "/";
        }

        conflictPath = conflictPath + nameWithoutExt + suffix + ext;
        if (!conflictPath.StartsWith('/'))
        {
            conflictPath = "/" + conflictPath;
        }

        // 原子写冲突副本（统一原子性：先 .tmp → rename，消除分块侧 IOFile.Copy 裸拷分叉）
        await _storage.AtomicWriteAsync(conflictPath, content, expectedHash: null);
        string conflictHash = await _storage.ComputeHashAsync(_storage.GetAbsolutePath(conflictPath));
        long fileSize = _storage.GetSize(conflictPath);
        var conflictEntry = await _index.UpsertFileAsync(
            conflictPath, FileType.File, conflictHash, fileSize,
            lastModified ?? DateTime.UtcNow.ToString("O"), conflictVersion,
            FileState.Conflict);

        // 审计日志（冲突）
        await _syncLog.LogAsync(path, SyncOperation.Upload, deviceId, LogResult.Conflict,
            $"客户端 v{baseVersion} vs 服务端 v{currentVersion}，冲突副本: {conflictEntry.Path}");

        return new ConflictSaveResult(conflictPath, currentVersion, baseVersion);
    }
}
