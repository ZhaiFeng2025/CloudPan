using CloudPan.Contract;
using CloudPan.Infrastructure.Models;
using CloudPan.Infrastructure.Storage;
using Microsoft.Extensions.Logging;

namespace CloudPan.Server.Core;

/// <inheritdoc />
public class FileOperationService : IFileOperationService
{
    private readonly IFileStorageService _storage;
    private readonly IFileIndexService _index;
    private readonly IVersionService _version;
    private readonly ITrashService _trash;
    private readonly ISyncLogService _syncLog;
    private readonly ConflictBackupHelper _conflictBackup;
    private readonly ILogger<FileOperationService> _logger;

    public FileOperationService(
        IFileStorageService storage,
        IFileIndexService index,
        IVersionService version,
        ITrashService trash,
        ISyncLogService syncLog,
        ConflictBackupHelper conflictBackup,
        ILogger<FileOperationService> logger)
    {
        _storage = storage;
        _index = index;
        _version = version;
        _trash = trash;
        _syncLog = syncLog;
        _conflictBackup = conflictBackup;
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

        // 分配新版本号（软删除墓碑据此传播给客户端增量同步）
        int newVersion = await _version.NextVersionAsync();

        // 先移入回收站（FS），成功后软删索引（DB）——DB 与 FS 任一步失败文件都保持可恢复：
        //   · 移入失败：索引未动、原文件保留并返回错误（不再物理删除兜底——回收站是删除唯一可恢复路径，
        //     兜底物理删除 = 静默永久丢数据，F-38；且不向客户端传播假删除，调用方可提示用户重试）；
        //   · 软删失败：把已移入回收站的文件回滚恢复原位，保持 DB 与 FS 一致。
        string metaFileName;
        try
        {
            metaFileName = await _trash.MoveToTrashAsync(path, isDirectory);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "移入回收站失败，保留原文件: {Path}", path);
            return new FileDeleteResult(false, null, null,
                new DomainError(HttpErrorCode.INTERNAL_ERROR, $"移入回收站失败: {ex.Message}", "删除失败，请稍后重试"));
        }

        try
        {
            // 软删除墓碑：FileEntry 行保留并标记 FileState.Deleting，客户端树查询据其删除本地副本
            await _index.SoftDeleteAsync(path, isDirectory, newVersion);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "软删除失败，回滚回收站移动: {Path}", path);
            var rollback = await _trash.RestoreAsync(metaFileName);
            if (!rollback.Success)
            {
                _logger.LogError("回滚回收站移动失败——文件已存于回收站（元数据 {Meta}），需手动恢复: {Path}（{Reason}）",
                    metaFileName, path, rollback.Error?.UserMessage ?? rollback.Error?.Message);
            }
            return new FileDeleteResult(false, null, null,
                new DomainError(HttpErrorCode.INTERNAL_ERROR, $"删除失败: {ex.Message}", "删除失败，请稍后重试"));
        }

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
        // T-069/F-78：目录条目全库统一无尾斜杠存储。TrimEnd('/') 规范化入库——Android 曾以
        // 尾斜杠拼接路径（/a/name/）入库，与 Windows 客户端无尾斜杠 mkdir（/a/name）对同一逻辑
        // 目录产生两个 FileEntry 行，导致索引错配与 FullScan 幽灵差异；此处单点兜底所有客户端。
        string dirPath = path.TrimEnd('/');
        if (dirPath.Length == 0)
        {
            dirPath = "/"; // 根路径边缘：TrimEnd 后勿成空串
        }

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
        // R-A5 路径安全统一防线：下载路径显式经 ValidatePath（与 Delete/Move/Mkdir 一致，
        // 防止 ../ 等路径穿越越界读取同步根外文件——如 .cloudpan 元数据/服务端配置）
        string? pathErr = _storage.ValidatePath(path);
        if (pathErr != null)
        {
            return new FileDownloadResult(false, null, null, null, 0,
                new DomainError(HttpErrorCode.BAD_REQUEST, pathErr, "路径格式不正确"));
        }

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
        // 『保存冲突副本』统一经领域辅助 ConflictBackupHelper（T-071：与分块上传 Finalize 行为一致，
        // 冲突检测 + 路径拼接 + 版本分配 + 原子写 + upsert + 审计单点实现）
        var conflict = await _conflictBackup.SaveConflictCopyIfNeededAsync(
            path, baseVersion, currentVersion, content, length, lastModified, deviceId);
        if (conflict == null)
        {
            // 防御：UploadService 已保证 baseVersion > 0 且服务端版本更高，理论不触发
            throw new InvalidOperationException(
                $"上传冲突处理被调用但未检测到冲突: path={path}, baseVersion={baseVersion}, currentVersion={currentVersion}");
        }

        return new UploadConflictResult(conflict.ConflictPath, conflict.CurrentVersion, conflict.BaseVersion);
    }
}
