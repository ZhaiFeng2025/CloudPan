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
public class FilesController : ControllerBase
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

        // 存档旧版本（如果文件已存在）
        var existingForArchive = await _index.GetByPathAsync(path);
        if (existingForArchive != null && existingForArchive.CurrentHash != null)
        {
            string deviceId = HttpContext.Items["DeviceId"] as string ?? "unknown";
            string storagePath = await _storage.StoreVersionAsync(path, existingForArchive.Version);
            await using var archiveDb = await _dbFactory.CreateDbContextAsync();
            archiveDb.VersionRecords.Add(new VersionRecord
            {
                FilePath = path,
                Version = existingForArchive.Version,
                Hash = existingForArchive.CurrentHash!,
                Size = existingForArchive.CurrentSize,
                StoragePath = storagePath,
                Timestamp = DateTime.UtcNow.ToString("O"),
                DeviceId = deviceId
            });

            // 保留最近 5 个版本
            var oldVersions = await archiveDb.VersionRecords
                .Where(v => v.FilePath == path)
                .OrderByDescending(v => v.Version)
                .Skip(5)
                .ToListAsync();
            archiveDb.VersionRecords.RemoveRange(oldVersions);
            await archiveDb.SaveChangesAsync();
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

        // 更新索引
        var entry = await _index.UpsertFileAsync(
            path, FileType.File, hash, file.Length,
            lastModified ?? DateTime.UtcNow.ToString("O"), newVersion);

        // 写入审计日志（上传成功）
        string uploadDeviceId = HttpContext.Items["DeviceId"] as string ?? "unknown";
        await _syncLog.LogAsync(entry.Path, SyncOperation.Upload, uploadDeviceId, LogResult.Success);

        // WebSocket 广播
        await _wsHandler.BroadcastFileChangedAsync(entry.Path, entry.Version, uploadDeviceId);

        return Ok(new
        {
            data = new
            {
                path = entry.Path,
                version = entry.Version,
                hash = entry.CurrentHash,
                size = entry.CurrentSize,
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

    /// <summary>
    /// POST /api/files/delete — 删除文件或文件夹（递归）。
    /// Phase 1a：检查 baseVersion 防冲突。
    /// </summary>
    [HttpPost("delete")]
    public async Task<IActionResult> Delete([FromBody] DeleteRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Path))
        {
            return this.Error(HttpErrorCode.BAD_REQUEST, "path 参数缺失", "请提供文件路径");
        }

        string? pathErr = _storage.ValidatePath(request.Path);
        if (pathErr != null)
        {
            return this.Error(HttpErrorCode.BAD_REQUEST, pathErr, "路径格式不正确");
        }

        var entry = await _index.GetByPathAsync(request.Path);
        if (entry == null)
        {
            return this.Error(HttpErrorCode.NOT_FOUND, $"文件不存在: {request.Path}", "文件未找到");
        }

        // 冲突检测
        if (request.BaseVersion > 0 && entry.Version > request.BaseVersion)
        {
            // 写入审计日志（删除冲突）
            string delConflictDeviceId = HttpContext.Items["DeviceId"] as string ?? "unknown";
            await _syncLog.LogAsync(request.Path, SyncOperation.Delete, delConflictDeviceId, LogResult.Conflict,
                $"服务端 v{entry.Version}，客户端 v{request.BaseVersion}");

            return this.Error(HttpErrorCode.CONFLICT,
                $"版本冲突：客户端基于 v{request.BaseVersion}，服务端当前 v{entry.Version}",
                "文件已被其他设备修改，请刷新后重试",
                detail: $"currentVersion={entry.Version}, baseVersion={request.BaseVersion}");
        }

        bool isDirectory = entry.Type == (int)FileType.Directory;

        // 移入回收站（而非永久删除）
        try
        {
            TrashController.MoveToTrash(_storage, request.Path, isDirectory);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "移入回收站失败: {Path}", request.Path); }

        // 删除 DB 条目
        await _index.DeleteAsync(request.Path, isDirectory);

        if (isDirectory)
        {
            try { _storage.DeleteDirectory(request.Path); }
            catch (Exception ex) { _logger.LogWarning(ex, "物理删除目录失败: {Path}", request.Path); }
        }
        else
        {
            try { _storage.Delete(request.Path); }
            catch (Exception ex) { _logger.LogWarning(ex, "物理删除文件失败: {Path}", request.Path); }
        }

        int newVersion = await _version.NextVersionAsync();

        // 写入审计日志（删除成功）
        string delDeviceId = HttpContext.Items["DeviceId"] as string ?? "unknown";
        await _syncLog.LogAsync(request.Path, SyncOperation.Delete, delDeviceId, LogResult.Success);

        // WebSocket 广播
        await _wsHandler.BroadcastFileDeletedAsync(request.Path, delDeviceId);

        return Ok(new
        {
            data = new { path = request.Path, deletedVersion = newVersion }
        });
    }

    /// <summary>
    /// POST /api/files/move — 移动或重命名文件/文件夹。
    /// </summary>
    [HttpPost("move")]
    public async Task<IActionResult> Move([FromBody] MoveRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.OldPath) || string.IsNullOrWhiteSpace(request.NewPath))
        {
            return this.Error(HttpErrorCode.BAD_REQUEST, "oldPath 和 newPath 参数缺失", "请提供源路径和目标路径");
        }

        string? err1 = _storage.ValidatePath(request.OldPath);
        string? err2 = _storage.ValidatePath(request.NewPath);
        if (err1 != null || err2 != null)
        {
            return this.Error(HttpErrorCode.BAD_REQUEST, (err1 ?? err2)!, "路径格式不正确"); // 上方已校验至少一个非空
        }

        var entry = await _index.GetByPathAsync(request.OldPath);
        if (entry == null)
        {
            return this.Error(HttpErrorCode.NOT_FOUND, $"文件不存在: {request.OldPath}", "文件未找到");
        }

        bool isDirectory = entry.Type == (int)FileType.Directory;

        // 先移动物理文件（失败则不更新索引）
        if (isDirectory)
        {
            string src = _storage.GetAbsolutePath(request.OldPath);
            string dst = _storage.GetAbsolutePath(request.NewPath);
            if (Directory.Exists(src))
            {
                Directory.Move(src, dst);
            }
        }
        else
        {
            _storage.Move(request.OldPath, request.NewPath);
        }

        int newVersion = await _version.NextVersionAsync();

        // 更新索引
        await _index.MoveAsync(request.OldPath, request.NewPath, newVersion, isDirectory);

        // 写入审计日志（重命名成功）
        string moveDeviceId = HttpContext.Items["DeviceId"] as string ?? "unknown";
        await _syncLog.LogAsync(request.NewPath, SyncOperation.Rename, moveDeviceId, LogResult.Success,
            $"重命名: {request.OldPath} → {request.NewPath}");

        // WebSocket 广播
        await _wsHandler.BroadcastFileRenamedAsync(request.OldPath, request.NewPath, moveDeviceId);

        return Ok(new
        {
            data = new { oldPath = request.OldPath, newPath = request.NewPath, version = newVersion }
        });
    }

    /// <summary>
    /// POST /api/files/mkdir — 创建文件夹。
    /// </summary>
    [HttpPost("mkdir")]
    public async Task<IActionResult> Mkdir([FromBody] MkdirRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Path))
        {
            return this.Error(HttpErrorCode.BAD_REQUEST, "path 参数缺失", "请提供文件夹路径");
        }

        string dirPath = request.Path;
        string? pathErr = _storage.ValidatePath(dirPath);
        if (pathErr != null)
        {
            return this.Error(HttpErrorCode.BAD_REQUEST, pathErr, "路径格式不正确");
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
            return Ok(new { data = new { path = dirPath } });
        }
        catch (InvalidOperationException)
        {
            return this.Error(HttpErrorCode.CONFLICT, $"路径已存在: {request.Path}", "该路径已存在，请更换名称");
        }
    }

    /// <summary>
    /// GET /api/files/search?q=... — 按文件名搜索。
    /// </summary>
    [HttpGet("search")]
    public async Task<IActionResult> Search([FromQuery] string q, [FromQuery] int limit = 50)
    {
        if (string.IsNullOrWhiteSpace(q) || q.Length < 2)
        {
            return this.Error(HttpErrorCode.BAD_REQUEST, "搜索关键词至少 2 个字符", "搜索关键词至少需要 2 个字符");
        }

        var results = await _index.SearchAsync(q, Math.Min(limit, 200));
        return Ok(new { data = results });
    }

    // ============================================================
    // 分块上传
    // ============================================================

    private const int ChunkSize = 4_194_304;            // 4MB
    private const long ChunkedUploadThreshold = 10_485_760; // 10MB
    private const int ChunkedUploadTimeoutMinutes = 1440;   // 24h

    /// <summary>
    /// POST /api/files/upload/chunk — 分块上传。
    /// 客户端将大文件拆分为 4MB 块，服务端按序接收，全部到达后合并为完整文件。
    /// 支持断点续传：已接收的块索引幂等跳过。
    /// </summary>
    [HttpPost("upload/chunk")]
    [RequestSizeLimit(5_000_000)] // 块数据 + form 开销
    public async Task<IActionResult> UploadChunk(
        [FromForm] IFormFile chunk,
        [FromForm] string path,
        [FromForm] int chunkIndex,
        [FromForm] int totalChunks,
        [FromForm] string fileHash,
        [FromForm] int baseVersion = 0,
        [FromForm] string? lastModified = null)
    {
        // 1. 参数校验
        if (chunk == null || chunk.Length == 0)
        {
            return this.Error(HttpErrorCode.BAD_REQUEST, "chunk 为空", "分块数据不能为空");
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return this.Error(HttpErrorCode.BAD_REQUEST, "path 参数缺失", "请提供文件路径");
        }

        if (string.IsNullOrWhiteSpace(fileHash))
        {
            return this.Error(HttpErrorCode.BAD_REQUEST, "fileHash 参数缺失", "缺少文件校验信息");
        }

        if (totalChunks <= 0 || chunkIndex < 0 || chunkIndex >= totalChunks)
        {
            return this.Error(HttpErrorCode.BAD_REQUEST, "chunkIndex/totalChunks 不合法", "分块参数不正确");
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

        string deviceId = HttpContext.Items["DeviceId"] as string ?? "unknown";

        await using var db = await _dbFactory.CreateDbContextAsync();

        // 2. 查找或创建 ChunkedUpload 记录
        ChunkedUpload? record;

        if (chunkIndex == 0)
        {
            // 清理超时的旧记录 + 临时文件
            string expiryTime = DateTime.UtcNow.AddMinutes(-ChunkedUploadTimeoutMinutes).ToString("O");
            var stale = await db.ChunkedUploads
                .Where(c => c.FilePath == path && string.Compare(c.CreatedAt, expiryTime) < 0)
                .ToListAsync();
            foreach (var s in stale)
            {
                SafeDeleteTemp(s.TempPath);
                db.ChunkedUploads.Remove(s);
            }

            // 检查是否已有同设备同路径的上传记录
            record = await db.ChunkedUploads.FindAsync(path);
            if (record != null)
            {
                if (record.DeviceId != deviceId)
                {
                    return this.Error(HttpErrorCode.CONFLICT, "另一设备正在上传该文件", "该文件正在被其他设备上传，请稍后重试");
                }
                // 同一设备：断点续传，重置数据
                record.TotalChunks = totalChunks;
                record.FileHash = fileHash;
                record.BaseVersion = baseVersion;
                record.LastModified = lastModified ?? DateTime.UtcNow.ToString("O");
                record.ReceivedChunks = "[]";
                record.CreatedAt = DateTime.UtcNow.ToString("O");
            }
            else
            {
                // 创建临时文件
                string tempDir = Path.Combine(
                    Path.GetDirectoryName(_storage.GetAbsolutePath(path))!,
                    ".cloudpan");
                Directory.CreateDirectory(tempDir);
                string tempPath = Path.Combine(tempDir, $"{Guid.NewGuid():N}.chunk.tmp");

                record = new ChunkedUpload
                {
                    FilePath = path,
                    DeviceId = deviceId,
                    FileHash = fileHash,
                    TotalChunks = totalChunks,
                    ReceivedChunks = "[]",
                    TempPath = tempPath,
                    BaseVersion = baseVersion,
                    LastModified = lastModified ?? DateTime.UtcNow.ToString("O"),
                    CreatedAt = DateTime.UtcNow.ToString("O")
                };
                db.ChunkedUploads.Add(record);
            }
            await db.SaveChangesAsync();
        }
        else
        {
            record = await db.ChunkedUploads.FindAsync(path);
            if (record == null)
            {
                return this.Error(HttpErrorCode.BAD_REQUEST, "分块上传会话不存在，请先传 chunkIndex=0", "分块上传会话已过期，请重新上传");
            }

            if (record.TotalChunks != totalChunks)
            {
                return this.Error(HttpErrorCode.BAD_REQUEST, "totalChunks 与首块不一致", "分块参数与首次上传不一致");
            }

            if (!string.Equals(record.FileHash, fileHash, StringComparison.OrdinalIgnoreCase))
            {
                return this.Error(HttpErrorCode.BAD_REQUEST, "fileHash 与首块不一致", "文件校验信息与首次上传不一致");
            }
        }

        // 3. 解析已接收块列表
        var received = System.Text.Json.JsonSerializer.Deserialize<List<int>>(record.ReceivedChunks)
                       ?? new List<int>();

        // 4. 幂等：已接收则跳过
        if (received.Contains(chunkIndex))
        {
            return Ok(new
            {
                data = new
                {
                    path,
                    chunkIndex,
                    receivedCount = received.Count,
                    totalChunks,
                    isComplete = received.Count == totalChunks
                }
            });
        }

        // 5. 写入块数据（追加到临时文件）
        await using (FileStream fs = new FileStream(record.TempPath, FileMode.Append, FileAccess.Write, FileShare.None))
        {
            await chunk.CopyToAsync(fs);
            await fs.FlushAsync();
        }

        received.Add(chunkIndex);
        record.ReceivedChunks = System.Text.Json.JsonSerializer.Serialize(received);
        await db.SaveChangesAsync();

        // 6. 判断是否最后一块
        if (received.Count == totalChunks)
        {
            return await FinalizeChunkedUploadAsync(db, record, path, fileHash, baseVersion, lastModified, deviceId);
        }

        return Ok(new
        {
            data = new
            {
                path,
                chunkIndex,
                receivedCount = received.Count,
                totalChunks,
                isComplete = false
            }
        });
    }

    /// <summary>分块全部到达：合并校验、冲突检测、存档、原子写入、索引更新。</summary>
    private async Task<IActionResult> FinalizeChunkedUploadAsync(
        CloudPanDbContext db, ChunkedUpload record, string path,
        string fileHash, int baseVersion, string? lastModified, string deviceId)
    {
        // a. 校验完整文件 SHA-256
        string actualHash = await _storage.ComputeHashAsync(record.TempPath);
        if (!string.Equals(actualHash, fileHash, StringComparison.OrdinalIgnoreCase))
        {
            SafeDeleteTemp(record.TempPath);
            db.ChunkedUploads.Remove(record);
            await db.SaveChangesAsync();
            return this.Error(HttpErrorCode.BAD_REQUEST,
                $"文件哈希校验失败。期望: {fileHash[..16]}..., 实际: {actualHash[..16]}...",
                "文件校验失败，请重新上传");
        }

        // b. 冲突检测
        if (baseVersion > 0)
        {
            var existing = await _index.GetByPathAsync(path);
            if (existing != null && existing.Version > baseVersion)
            {
                // 保存冲突副本
                int conflictVersion = await _version.NextVersionAsync();
                string nameWithoutExt = Path.GetFileNameWithoutExtension(path);
                string ext = Path.GetExtension(path);
                string suffix = DateTime.Now.ToString("_冲突_yyyyMMdd_HHmmss");
                string conflictPath = (Path.GetDirectoryName(path)?.Replace('\\', '/') ?? "");
                if (!conflictPath.EndsWith('/') && !string.IsNullOrEmpty(conflictPath))
                {
                    conflictPath += "/";
                }

                conflictPath = conflictPath + nameWithoutExt + suffix + ext;
                if (!conflictPath.StartsWith('/'))
                {
                    conflictPath = "/" + conflictPath;
                }

                IOFile.Copy(record.TempPath, _storage.GetAbsolutePath(conflictPath), overwrite: true);
                string conflictHash = await _storage.ComputeHashAsync(_storage.GetAbsolutePath(conflictPath));
                long fileSize = new FileInfo(_storage.GetAbsolutePath(conflictPath)).Length;
                var conflictEntry = await _index.UpsertFileAsync(
                    conflictPath, FileType.File, conflictHash, fileSize,
                    lastModified ?? DateTime.UtcNow.ToString("O"), conflictVersion,
                    FileState.Conflict);

                SafeDeleteTemp(record.TempPath);
                db.ChunkedUploads.Remove(record);
                await db.SaveChangesAsync();

                // 审计日志（冲突）
                await _syncLog.LogAsync(path, SyncOperation.Upload, deviceId, LogResult.Conflict,
                    $"客户端 v{baseVersion} vs 服务端 v{existing.Version}，冲突副本: {conflictPath}");

                return this.Error(HttpErrorCode.CONFLICT,
                    $"版本冲突：客户端基于 v{baseVersion}，服务端当前 v{existing.Version}",
                    "文件已被其他设备修改，请刷新后重试",
                    detail: $"currentVersion={existing.Version}, baseVersion={baseVersion}, conflictPath={conflictEntry.Path}");
            }
        }

        // c. 存档旧版本
        await using var tx = await db.Database.BeginTransactionAsync();

        var existingForArchive = await _index.GetByPathAsync(path);
        if (existingForArchive != null && existingForArchive.CurrentHash != null)
        {
            string storagePath = await _storage.StoreVersionAsync(path, existingForArchive.Version);
            db.VersionRecords.Add(new VersionRecord
            {
                FilePath = path,
                Version = existingForArchive.Version,
                Hash = existingForArchive.CurrentHash!,
                Size = existingForArchive.CurrentSize,
                StoragePath = storagePath,
                Timestamp = DateTime.UtcNow.ToString("O"),
                DeviceId = deviceId
            });

            // 保留最近 5 个版本
            var oldVersions = await db.VersionRecords
                .Where(v => v.FilePath == path)
                .OrderByDescending(v => v.Version)
                .Skip(5)
                .ToListAsync();
            db.VersionRecords.RemoveRange(oldVersions);
        }

        // d. 分配版本号 + 原子写入
        int newVersion = await _version.NextVersionAsync();
        string targetPath = _storage.GetAbsolutePath(path);
        string? dir = Path.GetDirectoryName(targetPath);
        if (dir != null)
        {
            Directory.CreateDirectory(dir);
        }

        IOFile.Move(record.TempPath, targetPath, overwrite: true);

        // e. 更新索引
        string hash = await _storage.ComputeHashAsync(targetPath);
        var entry = await _index.UpsertFileAsync(
            path, FileType.File, hash, new FileInfo(targetPath).Length,
            record.LastModified, newVersion);

        // f. 清理 ChunkedUpload 记录
        db.ChunkedUploads.Remove(record);
        await db.SaveChangesAsync();
        await tx.CommitAsync();

        // 审计日志
        await _syncLog.LogAsync(entry.Path, SyncOperation.Upload, deviceId, LogResult.Success);

        return Ok(new
        {
            data = new
            {
                path = entry.Path,
                version = entry.Version,
                hash = entry.CurrentHash,
                size = entry.CurrentSize,
                status = "complete"
            }
        });
    }

    /// <summary>
    /// GET /api/files/upload/chunk/status — 查询分块上传进度。
    /// </summary>
    [HttpGet("upload/chunk/status")]
    public async Task<IActionResult> GetChunkStatus([FromQuery] string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return this.Error(HttpErrorCode.BAD_REQUEST, "path 参数缺失", "请提供文件路径");
        }

        if (!path.StartsWith('/'))
        {
            path = "/" + path;
        }

        await using var db = await _dbFactory.CreateDbContextAsync();
        var record = await db.ChunkedUploads.FindAsync(path);

        if (record == null)
        {
            return Ok(new
            {
                data = new
                {
                    path,
                    receivedChunks = Array.Empty<int>(),
                    totalChunks = 0,
                    isComplete = false
                }
            });
        }

        var received = System.Text.Json.JsonSerializer.Deserialize<List<int>>(record.ReceivedChunks) ?? new List<int>();

        return Ok(new
        {
            data = new
            {
                record.FilePath,
                receivedChunks = received,
                record.TotalChunks,
                isComplete = received.Count == record.TotalChunks,
                record.DeviceId,
                record.CreatedAt
            }
        });
    }

    /// <summary>安全删除临时文件。</summary>
    private static void SafeDeleteTemp(string path)
    {
        try { if (IOFile.Exists(path))
            {
                IOFile.Delete(path);
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"删除临时文件失败: {path} - {ex.Message}"); }
    }
}

// ---- 请求 DTO（简单场景直接用 record，不放入共享库） ----

/// <summary>删除文件请求（BaseVersion 用于乐观并发校验，0 表示不校验）。</summary>
public record DeleteRequest(string Path, int BaseVersion = 0);

/// <summary>移动/重命名请求（BaseVersion 用于乐观并发校验，0 表示不校验）。</summary>
public record MoveRequest(string OldPath, string NewPath, int BaseVersion = 0);

/// <summary>创建目录请求。</summary>
public record MkdirRequest(string Path);
