using CloudPan.Server.Models;
using CloudPan.Shared;
using Microsoft.Extensions.Logging;

namespace CloudPan.Server.Services;

/// <inheritdoc />
public class FileOperationService : IFileOperationService
{
    private readonly IFileStorageService _storage;
    private readonly IFileIndexService _index;
    private readonly IVersionService _version;
    private readonly ITrashService _trash;
    private readonly ISyncLogService _syncLog;
    private readonly ILogger<FileOperationService> _logger;

    public FileOperationService(
        IFileStorageService storage,
        IFileIndexService index,
        IVersionService version,
        ITrashService trash,
        ISyncLogService syncLog,
        ILogger<FileOperationService> logger)
    {
        _storage = storage;
        _index = index;
        _version = version;
        _trash = trash;
        _syncLog = syncLog;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<FileDeleteResult> DeleteAsync(string path, int baseVersion, string deviceId)
    {
        // 路径安全统一防线
        string? pathErr = _storage.ValidatePath(path);
        if (pathErr != null)
        {
            return new FileDeleteResult(false, null, null,
                new DomainError(HttpErrorCode.BAD_REQUEST, pathErr, "路径格式不正确"));
        }

        var entry = await _index.GetByPathAsync(path);
        if (entry == null)
        {
            return new FileDeleteResult(false, null, null,
                new DomainError(HttpErrorCode.NOT_FOUND, $"文件不存在: {path}", "文件未找到"));
        }

        // 冲突检测
        if (baseVersion > 0 && entry.Version > baseVersion)
        {
            // 写入审计日志（删除冲突）
            await _syncLog.LogAsync(path, SyncOperation.Delete, deviceId, LogResult.Conflict,
                $"服务端 v{entry.Version}，客户端 v{baseVersion}");

            return new FileDeleteResult(false, null, null,
                new DomainError(HttpErrorCode.CONFLICT,
                    $"版本冲突：客户端基于 v{baseVersion}，服务端当前 v{entry.Version}",
                    "文件已被其他设备修改，请刷新后重试",
                    Detail: $"currentVersion={entry.Version}, baseVersion={baseVersion}"));
        }

        bool isDirectory = entry.Type == (int)FileType.Directory;

        // 先删除 DB 条目（失败则抛异常，文件保持原样，索引与 FS 一致）
        await _index.DeleteAsync(path, isDirectory);

        // 再移入回收站（FS）；失败则物理删除兜底，避免孤儿文件
        try
        {
            await _trash.MoveToTrashAsync(path, isDirectory);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "移入回收站失败，尝试物理删除: {Path}", path);
            try
            {
                if (isDirectory) { _storage.DeleteDirectory(path); }
                else { _storage.Delete(path); }
            }
            catch (Exception ex2) { _logger.LogWarning(ex2, "物理删除失败: {Path}", path); }
        }

        int newVersion = await _version.NextVersionAsync();

        // 写入审计日志（删除成功）
        await _syncLog.LogAsync(path, SyncOperation.Delete, deviceId, LogResult.Success);

        return new FileDeleteResult(true, path, newVersion);
    }

    /// <inheritdoc />
    public async Task<FileMoveResult> MoveAsync(string oldPath, string newPath, int baseVersion, string deviceId)
    {
        string? err1 = _storage.ValidatePath(oldPath);
        string? err2 = _storage.ValidatePath(newPath);
        if (err1 != null || err2 != null)
        {
            return new FileMoveResult(false, null, null, null,
                new DomainError(HttpErrorCode.BAD_REQUEST, (err1 ?? err2)!, "路径格式不正确")); // 上方已校验至少一个非空
        }

        var entry = await _index.GetByPathAsync(oldPath);
        if (entry == null)
        {
            return new FileMoveResult(false, null, null, null,
                new DomainError(HttpErrorCode.NOT_FOUND, $"文件不存在: {oldPath}", "文件未找到"));
        }

        bool isDirectory = entry.Type == (int)FileType.Directory;

        // 先执行 DB 索引更新（不含审计日志），成功后再移动物理文件，最后写入审计日志
        int newVersion = await _version.NextVersionAsync();
        await _index.MoveAsync(oldPath, newPath, newVersion, isDirectory);

        // 移动物理文件——失败时回滚 DB 索引
        try
        {
            if (isDirectory)
            {
                string src = _storage.GetAbsolutePath(oldPath);
                string dst = _storage.GetAbsolutePath(newPath);
                if (Directory.Exists(src))
                {
                    Directory.Move(src, dst);
                }
            }
            else
            {
                _storage.Move(oldPath, newPath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "物理文件移动失败，正在回滚 DB 索引: {Old} → {New}", oldPath, newPath);
            // 回滚 DB：将索引移回原路径
            try { await _index.MoveAsync(newPath, oldPath, newVersion, isDirectory); }
            catch (Exception rollbackEx) { _logger.LogError(rollbackEx, "回滚 DB 索引失败——需手动修复: {Old}", oldPath); }
            return new FileMoveResult(false, null, null, null,
                new DomainError(HttpErrorCode.INTERNAL_ERROR, $"文件移动失败: {ex.Message}", "文件移动失败，请检查磁盘空间和权限"));
        }

        // 物理文件移动成功后写入审计日志（避免物理移动失败时日志仍显示"成功"）
        await _syncLog.LogAsync(newPath, SyncOperation.Rename, deviceId, LogResult.Success,
            $"重命名: {oldPath} → {newPath}");

        return new FileMoveResult(true, oldPath, newPath, newVersion);
    }

    /// <inheritdoc />
    public async Task<FileMkdirResult> MkdirAsync(string path)
    {
        string dirPath = path;
        string? pathErr = _storage.ValidatePath(dirPath);
        if (pathErr != null)
        {
            return new FileMkdirResult(false, null,
                new DomainError(HttpErrorCode.BAD_REQUEST, pathErr, "路径格式不正确"));
        }

        if (!dirPath.StartsWith('/'))
        {
            dirPath = "/" + dirPath;
        }

        // 确保以 / 结尾
        if (!dirPath.EndsWith('/'))
        {
            dirPath += "/";
        }

        try
        {
            _storage.CreateDirectory(dirPath);
            int dirVersion = await _version.NextVersionAsync();
            await _index.CreateDirectoryAsync(dirPath, dirVersion);
            return new FileMkdirResult(true, dirPath);
        }
        catch (InvalidOperationException)
        {
            return new FileMkdirResult(false, null,
                new DomainError(HttpErrorCode.CONFLICT, $"路径已存在: {path}", "该路径已存在，请更换名称"));
        }
    }

    /// <inheritdoc />
    public async Task<FileDownloadResult> DownloadAsync(string path)
    {
        var entry = await _index.GetByPathAsync(path);
        if (entry == null)
        {
            return new FileDownloadResult(false, null, null, null, 0,
                new DomainError(HttpErrorCode.NOT_FOUND, $"文件不存在: {path}", "文件未找到"));
        }

        if (entry.Type == (int)FileType.Directory)
        {
            return new FileDownloadResult(false, null, null, null, 0,
                new DomainError(HttpErrorCode.BAD_REQUEST, "不能下载目录", "目录不能直接下载，请选择具体文件"));
        }

        if (!_storage.Exists(path))
        {
            return new FileDownloadResult(false, null, null, null, 0,
                new DomainError(HttpErrorCode.NOT_FOUND, $"文件不存在: {path}", "文件未找到"));
        }

        var stream = _storage.OpenRead(path);
        string fileName = Path.GetFileName(path);
        long size = _storage.GetSize(path);

        return new FileDownloadResult(true, entry, stream, fileName, size);
    }

    /// <inheritdoc />
    public async Task<UploadConflictResult> HandleUploadConflictAsync(
        string path, Stream content, long length, string? lastModified,
        int baseVersion, int currentVersion, string deviceId)
    {
        int conflictVersion = await _version.NextVersionAsync();

        // 生成冲突文件名
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

        // 保存冲突副本
        await _storage.AtomicWriteAsync(conflictPath, content, expectedHash: null);

        string conflictHash = await _storage.ComputeHashAsync(_storage.GetAbsolutePath(conflictPath));
        var conflictEntry = await _index.UpsertFileAsync(
            conflictPath, FileType.File, conflictHash, length,
            lastModified ?? DateTime.UtcNow.ToString("O"), conflictVersion,
            FileState.Conflict);

        // 写入审计日志（冲突）
        await _syncLog.LogAsync(path, SyncOperation.Upload, deviceId, LogResult.Conflict,
            $"客户端 v{baseVersion} vs 服务端 v{currentVersion}，冲突副本: {conflictEntry.Path}");

        return new UploadConflictResult(conflictPath, currentVersion, baseVersion);
    }
}
