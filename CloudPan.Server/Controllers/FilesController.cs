using Microsoft.AspNetCore.Mvc;
using CloudPan.Shared;
using CloudPan.Server.Services;

namespace CloudPan.Server.Controllers;

/// <summary>
/// 文件操作 API：上传、下载、文件树、删除、移动、创建文件夹、搜索。
/// Phase 0：无 Token 认证，无冲突检测，无版本历史。
/// </summary>
[ApiController]
[Route("api/files")]
public class FilesController : ControllerBase
{
    private readonly FileStorageService _storage;
    private readonly FileIndexService _index;
    private readonly VersionService _version;

    public FilesController(FileStorageService storage, FileIndexService index, VersionService version)
    {
        _storage = storage;
        _index = index;
        _version = version;
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
    /// Phase 0：不校验 baseVersion，直接覆盖写入。
    /// </summary>
    [HttpPost("upload")]
    [RequestSizeLimit(50_000_000)] // 50MB max for Phase 0 (no chunked upload yet)
    public async Task<IActionResult> Upload(
        IFormFile file,
        [FromForm] string path,
        [FromForm] int baseVersion = 0,
        [FromForm] string? lastModified = null)
    {
        if (file == null || file.Length == 0)
            return BadRequest(new { error = new { code = "BAD_REQUEST", message = "文件为空" } });

        if (string.IsNullOrWhiteSpace(path))
            return BadRequest(new { error = new { code = "BAD_REQUEST", message = "path 参数缺失" } });

        if (!path.StartsWith('/')) path = "/" + path;

        var pathErr = _storage.ValidatePath(path);
        if (pathErr != null)
            return BadRequest(new { error = new { code = "BAD_REQUEST", message = pathErr } });

        // 先分配版本号，再写文件，避免孤儿文件
        var newVersion = await _version.NextVersionAsync();

        // 写入文件
        await using var stream = file.OpenReadStream();
        var writeError = await _storage.AtomicWriteAsync(path, stream, expectedHash: null);
        if (writeError != null)
            return StatusCode(500, new { error = new { code = "INTERNAL_ERROR", message = writeError } });

        // 计算哈希和大小
        var hash = await _storage.ComputeHashAsync(_storage.GetAbsolutePath(path));

        // 更新索引
        var entry = await _index.UpsertFileAsync(
            path, FileType.File, hash, file.Length,
            lastModified ?? DateTime.UtcNow.ToString("O"), newVersion);

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

    /// <summary>
    /// GET /api/files/download?path=... — 下载文件。
    /// </summary>
    [HttpGet("download")]
    public async Task<IActionResult> Download([FromQuery] string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return BadRequest(new { error = new { code = "BAD_REQUEST", message = "path 参数缺失" } });

        var entry = await _index.GetByPathAsync(path);
        if (entry == null)
            return NotFound(new { error = new { code = "NOT_FOUND", message = $"文件不存在: {path}" } });
        if (entry.Type == (int)FileType.Directory)
            return BadRequest(new { error = new { code = "BAD_REQUEST", message = "不能下载目录" } });

        if (!_storage.Exists(path))
            return NotFound(new { error = new { code = "NOT_FOUND", message = $"文件不存在: {path}" } });
        var absolutePath = _storage.GetAbsolutePath(path);
        var stream = _storage.OpenRead(path);
        var fileName = Path.GetFileName(path);

        Response.Headers["X-File-Hash"] = entry?.CurrentHash ?? "";
        Response.Headers["X-File-Version"] = entry?.Version.ToString() ?? "0";
        Response.Headers["X-File-Size"] = _storage.GetSize(path).ToString();
        Response.Headers["X-File-Modified"] = entry?.LastModified ?? "";

        return File(stream, "application/octet-stream", fileName);
    }

    /// <summary>
    /// POST /api/files/delete — 删除文件或文件夹（递归）。
    /// </summary>
    [HttpPost("delete")]
    public async Task<IActionResult> Delete([FromBody] DeleteRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Path))
            return BadRequest(new { error = new { code = "BAD_REQUEST", message = "path 参数缺失" } });

        var pathErr = _storage.ValidatePath(request.Path);
        if (pathErr != null)
            return BadRequest(new { error = new { code = "BAD_REQUEST", message = pathErr } });

        var entry = await _index.GetByPathAsync(request.Path);
        if (entry == null)
            return NotFound(new { error = new { code = "NOT_FOUND", message = $"文件不存在: {request.Path}" } });

        var isDirectory = entry.Type == (int)FileType.Directory;

        // 先删除物理文件，再删除 DB（物理操作失败不阻塞 DB）
        if (isDirectory)
        {
            try { _storage.DeleteDirectory(request.Path); } catch { }
        }
        else
        {
            try { _storage.Delete(request.Path); } catch { }
        }

        // 删除 DB 条目
        await _index.DeleteAsync(request.Path, isDirectory);

        var newVersion = await _version.NextVersionAsync();

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
            return BadRequest(new { error = new { code = "BAD_REQUEST", message = "oldPath 和 newPath 参数缺失" } });

        var err1 = _storage.ValidatePath(request.OldPath);
        var err2 = _storage.ValidatePath(request.NewPath);
        if (err1 != null || err2 != null)
            return BadRequest(new { error = new { code = "BAD_REQUEST", message = err1 ?? err2 } });

        var entry = await _index.GetByPathAsync(request.OldPath);
        if (entry == null)
            return NotFound(new { error = new { code = "NOT_FOUND", message = $"文件不存在: {request.OldPath}" } });

        var isDirectory = entry.Type == (int)FileType.Directory;

        // 先移动物理文件（失败则不更新索引）
        if (isDirectory)
        {
            var src = _storage.GetAbsolutePath(request.OldPath);
            var dst = _storage.GetAbsolutePath(request.NewPath);
            if (Directory.Exists(src))
                Directory.Move(src, dst);
        }
        else
        {
            _storage.Move(request.OldPath, request.NewPath);
        }

        var newVersion = await _version.NextVersionAsync();

        // 更新索引
        await _index.MoveAsync(request.OldPath, request.NewPath, newVersion, isDirectory);

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
            return BadRequest(new { error = new { code = "BAD_REQUEST", message = "path 参数缺失" } });

        var dirPath = request.Path;
        var pathErr = _storage.ValidatePath(dirPath);
        if (pathErr != null)
            return BadRequest(new { error = new { code = "BAD_REQUEST", message = pathErr } });
        if (!dirPath.StartsWith('/'))
            dirPath = "/" + dirPath;

        // 确保以 / 结尾
        if (!dirPath.EndsWith('/'))
            dirPath += "/";

        try
        {
            _storage.CreateDirectory(dirPath);
            var dirVersion = await _version.NextVersionAsync();
            await _index.CreateDirectoryAsync(dirPath, dirVersion);
            return Ok(new { data = new { path = dirPath } });
        }
        catch (InvalidOperationException)
        {
            return Conflict(new { error = new { code = "CONFLICT", message = $"路径已存在: {request.Path}" } });
        }
    }

    /// <summary>
    /// GET /api/files/search?q=... — 按文件名搜索。
    /// </summary>
    [HttpGet("search")]
    public async Task<IActionResult> Search([FromQuery] string q, [FromQuery] int limit = 50)
    {
        if (string.IsNullOrWhiteSpace(q) || q.Length < 2)
            return BadRequest(new { error = new { code = "BAD_REQUEST", message = "搜索关键词至少 2 个字符" } });

        var results = await _index.SearchAsync(q, Math.Min(limit, 200));
        return Ok(new { data = results });
    }
}

// ---- 请求 DTO（简单场景直接用 record，不放入共享库） ----

public record DeleteRequest(string Path, int BaseVersion = 0);

public record MoveRequest(string OldPath, string NewPath, int BaseVersion = 0);

public record MkdirRequest(string Path);
