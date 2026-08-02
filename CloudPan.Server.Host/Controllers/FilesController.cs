using CloudPan.Server;
using CloudPan.Server.Data;
using CloudPan.Server.Models;
using CloudPan.Server.Services;
using CloudPan.Shared;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using IOFile = System.IO.File;

namespace CloudPan.Server.Controllers;

/// <summary>
/// 文件操作 API：上传、下载、文件树、删除、移动、创建文件夹、搜索。
/// Phase 1b：上传前自动存档旧版本（版本历史）。
/// </summary>
[ApiController]
[Route("api/files")]
[EndpointAuth(AuthMode.Token)]
public partial class FilesController : ControllerBase
{
    private readonly IFileStorageService _storage;
    private readonly IFileIndexService _index;
    private readonly IVersionService _version;
    private readonly IDbContextFactory<CloudPanDbContext> _dbFactory;
    private readonly ISyncLogService _syncLog;
    private readonly IWebSocketHandler _wsHandler;
    private readonly ILogger<FilesController> _logger;

    public FilesController(
        IFileStorageService storage,
        IFileIndexService index,
        IVersionService version,
        IDbContextFactory<CloudPanDbContext> dbFactory,
        ISyncLogService syncLog,
        IWebSocketHandler wsHandler,
        ILogger<FilesController> logger)
    {
        _storage = storage;
        _index = index;
        _version = version;
        _dbFactory = dbFactory;
        _syncLog = syncLog;
        _wsHandler = wsHandler;
        _logger = logger;
    }

    /// <summary>
    /// GET /api/files/tree — 获取文件树（含哈希和版本信息）。
    /// 支持 sinceVersion 增量拉取、cursor 分页。
    /// </summary>
    [HttpGet("tree")]
    public async Task<IActionResult> GetTree(
        [FromQuery] int? sinceVersion = null,
        [FromQuery] string? path = null,
        [FromQuery] int limit = 5000,
        [FromQuery] string? cursor = null)
    {
        var result = await _index.GetFileTreeAsync(sinceVersion, path, Math.Min(limit, 10000), cursor);
        return Ok(new
        {
            data = result.Data,
            nextCursor = result.NextCursor,
            hasMore = result.HasMore,
            maxVersion = result.MaxVersion
        });
    }

    /// <summary>
    /// POST /api/files/upload — 上传文件（multipart）。
    /// Phase 1a：校验 baseVersion，版本不匹配时保存冲突副本。
    /// </summary>
    [HttpPost("upload")]
    [RequestSizeLimit(50_000_000)]
    public async Task<IActionResult> Upload(
        IFormFile file,
        [FromForm] string path,
        [FromForm] int baseVersion = 0,
        [FromForm] string? lastModified = null)
    {
        if (file == null || file.Length == 0)
        {
            return this.Error(HttpErrorCode.BAD_REQUEST, "文件为空", "文件不能为空");
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return this.Error(HttpErrorCode.BAD_REQUEST, "path 参数缺失", "请提供文件路径");
        }

        if (!path.StartsWith('/'))
        {
            path = "/" + path;
        }

        string? pathErr = _storage.ValidatePath(path);
        if (pathErr != null)
        {
            return this.Error(HttpErrorCode.BAD_REQUEST, pathErr, "路径格式不正确");
        }

        // 冲突检测：baseVersion > 0 且当前版本 > baseVersion → 冲突
        if (baseVersion > 0)
        {
            var existing = await _index.GetByPathAsync(path);
            if (existing != null && existing.Version > baseVersion)
            {
                return await HandleUploadConflictAsync(file, path, existing, lastModified, baseVersion);
            }
        }

        // 先分配版本号，再写文件，避免孤儿文件
        int newVersion = await _version.NextVersionAsync();

        // 写入文件
        await using var stream = file.OpenReadStream();
        string? writeError = await _storage.AtomicWriteAsync(path, stream, expectedHash: null);
        if (writeError != null)
        {
            return this.Error(HttpErrorCode.INTERNAL_ERROR, writeError, "服务暂时不可用，请稍后重试");
        }

        // 计算哈希和大小
        string hash = await _storage.ComputeHashAsync(_storage.GetAbsolutePath(path));

        // 存档旧版本 + 更新索引 + 审计日志（同一 DbContext 事务；存档 FS 步骤在 try 内——异常统一清理目标文件与存档，避免孤儿）
        string uploadDeviceId = HttpContext.Items["DeviceId"] as string ?? "unknown";
        await using var db = await _dbFactory.CreateDbContextAsync();

        string? archiveStoragePath = null;
        await using var tx = await db.Database.BeginTransactionAsync();

        try
        {
            // 1. 存档旧版本（FS；在 try 内，存档异常走 catch 清理目标文件，避免孤儿）
            var existingForArchive = await db.FileEntries.FindAsync(path);
            if (existingForArchive != null && existingForArchive.CurrentHash != null)
            {
                archiveStoragePath = await _storage.StoreVersionAsync(path, existingForArchive.Version);
            }

            // 2. DB 插入 VersionRecord（仅当 FS 存档已完成）
            if (existingForArchive != null && existingForArchive.CurrentHash != null)
            {
                db.VersionRecords.Add(new VersionRecord
                {
                    FilePath = path,
                    Version = existingForArchive.Version,
                    Hash = existingForArchive.CurrentHash!,
                    Size = existingForArchive.CurrentSize,
                    StoragePath = archiveStoragePath!,
                    Timestamp = DateTime.UtcNow.ToString("O"),
                    DeviceId = uploadDeviceId
                });

                // 保留最近 5 个版本
                var oldVersions = await db.VersionRecords
                    .Where(v => v.FilePath == path)
                    .OrderByDescending(v => v.Version)
                    .Skip(5)
                    .ToListAsync();
                db.VersionRecords.RemoveRange(oldVersions);
            }

            // 3. 更新文件索引
            FileEntry? entry = await db.FileEntries.FindAsync(path);
            if (entry != null)
            {
                entry.CurrentHash = hash;
                entry.CurrentSize = file.Length;
                entry.Version = newVersion;
                entry.LastModified = lastModified ?? DateTime.UtcNow.ToString("O");
                entry.State = (int)FileState.Synced;
            }
            else
            {
                entry = new FileEntry
                {
                    Path = path,
                    Type = (int)FileType.File,
                    CurrentHash = hash,
                    CurrentSize = file.Length,
                    Version = newVersion,
                    LastModified = lastModified ?? DateTime.UtcNow.ToString("O"),
                    State = (int)FileState.Synced,
                    CreatedAt = DateTime.UtcNow.ToString("O")
                };
                db.FileEntries.Add(entry);
            }

            // 4. 审计日志
            db.SyncLogs.Add(new SyncLog
            {
                FilePath = entry.Path,
                Operation = (int)SyncOperation.Upload,
                DeviceId = uploadDeviceId,
                Result = (int)LogResult.Success,
                CreatedAt = DateTime.UtcNow.ToString("O")
            });

            await db.SaveChangesAsync();
            await tx.CommitAsync();
        }
        catch
        {
            await tx.RollbackAsync();
            // 事务回滚后删除已写入的文件 + 版本存档，避免孤儿文件
            try { _storage.Delete(path); } catch (Exception cleanupEx) { _logger.LogWarning(cleanupEx, "事务回滚后清理文件失败: {Path}", path); }
            if (archiveStoragePath != null) { try { _storage.Delete(archiveStoragePath); } catch (Exception cleanupEx) { _logger.LogWarning(cleanupEx, "事务回滚后清理版本存档失败: {Path}", archiveStoragePath); } }
            throw;
        }

        // WebSocket 广播
        await _wsHandler.BroadcastFileChangedAsync(path, newVersion, uploadDeviceId);

        // 获取最新 entry 构造响应
        var savedEntry = await _index.GetByPathAsync(path);
        return Ok(new
        {
            data = new
            {
                path = savedEntry?.Path ?? path,
                version = newVersion,
                hash,
                size = file.Length,
                conflictResolved = false
            }
        });
    }

    /// <summary>上传冲突处理：保存冲突副本（_冲突_yyyyMMdd_HHmmss）。</summary>
    private async Task<IActionResult> HandleUploadConflictAsync(
        IFormFile file, string path, FileEntry existing, string? lastModified, int baseVersion)
    {
        int conflictVersion = await _version.NextVersionAsync();

        // 生成冲突文件名
        string nameWithoutExt = Path.GetFileNameWithoutExtension(path);
        string ext = Path.GetExtension(path);
        string suffix = DateTime.Now.ToString("_冲突_yyyyMMdd_HHmmss"); // spec: conflictSuffixPattern
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
        await using var stream = file.OpenReadStream();
        await _storage.AtomicWriteAsync(conflictPath, stream, expectedHash: null);

        string conflictHash = await _storage.ComputeHashAsync(_storage.GetAbsolutePath(conflictPath));
        var conflictEntry = await _index.UpsertFileAsync(
            conflictPath, FileType.File, conflictHash, file.Length,
            lastModified ?? DateTime.UtcNow.ToString("O"), conflictVersion,
            FileState.Conflict);

        // 写入审计日志（冲突）
        string conflictDeviceId = HttpContext.Items["DeviceId"] as string ?? "unknown";
        await _syncLog.LogAsync(path, SyncOperation.Upload, conflictDeviceId, LogResult.Conflict,
            $"客户端 v{baseVersion} vs 服务端 v{existing.Version}，冲突副本: {conflictEntry.Path}");

        return this.Error(HttpErrorCode.CONFLICT,
            $"版本冲突：客户端基于 v{baseVersion}，服务端当前 v{existing.Version}",
            "文件已被其他设备修改，请刷新后重试",
            detail: $"currentVersion={existing.Version}, baseVersion={baseVersion}, conflictPath={conflictEntry.Path}");
    }

    /// <summary>
    /// GET /api/files/download?path=... — 下载文件。
    /// </summary>
    [HttpGet("download")]
    public async Task<IActionResult> Download([FromQuery] string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return this.Error(HttpErrorCode.BAD_REQUEST, "path 参数缺失", "请提供文件路径");
        }

        var entry = await _index.GetByPathAsync(path);
        if (entry == null)
        {
            return this.Error(HttpErrorCode.NOT_FOUND, $"文件不存在: {path}", "文件未找到");
        }

        if (entry.Type == (int)FileType.Directory)
        {
            return this.Error(HttpErrorCode.BAD_REQUEST, "不能下载目录", "目录不能直接下载，请选择具体文件");
        }

        if (!_storage.Exists(path))
        {
            return this.Error(HttpErrorCode.NOT_FOUND, $"文件不存在: {path}", "文件未找到");
        }

        string absolutePath = _storage.GetAbsolutePath(path);
        var stream = _storage.OpenRead(path);
        string fileName = Path.GetFileName(path);

        Response.Headers["X-File-Hash"] = entry?.CurrentHash ?? "";
        Response.Headers["X-File-Version"] = entry?.Version.ToString() ?? "0";
        Response.Headers["X-File-Size"] = _storage.GetSize(path).ToString();
        Response.Headers["X-File-Modified"] = entry?.LastModified ?? "";

        return File(stream, "application/octet-stream", fileName);
    }
}

// ---- 请求 DTO（简单场景直接用 record，不放入共享库） ----

/// <summary>删除文件请求（BaseVersion 用于乐观并发校验，0 表示不校验）。</summary>
public record DeleteRequest(string Path, int BaseVersion = 0);

/// <summary>移动/重命名请求（BaseVersion 用于乐观并发校验，0 表示不校验）。</summary>
public record MoveRequest(string OldPath, string NewPath, int BaseVersion = 0);

/// <summary>创建目录请求。</summary>
public record MkdirRequest(string Path);
