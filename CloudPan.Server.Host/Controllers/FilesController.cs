using CloudPan.Contract;
using CloudPan.Infrastructure.Storage;
using CloudPan.Server.Core;
using CloudPan.Server.Host;
using Microsoft.AspNetCore.Mvc;

namespace CloudPan.Server.Host.Controllers;

/// <summary>
/// 文件操作 API：上传、下载、文件树、删除、移动、创建文件夹、搜索。
/// 只做参数绑定与状态码适配——上传编排在 IUploadService、其余文件操作在 IFileOperationService，
/// 删除/移动/建目录在 FilesController.FileOps.cs，分块上传在 FilesController.ChunkedUpload.cs。
/// </summary>
[ApiController]
[Route("api/files")]
[EndpointAuth(AuthMode.Token)]
public partial class FilesController : ControllerBase
{
    private readonly IFileStorageService _storage;
    private readonly IFileIndexService _index;
    private readonly IUploadService _upload;
    private readonly IFileOperationService _fileOps;
    private readonly IChunkedUploadService _chunkedUpload;
    private readonly IWebSocketHandler _wsHandler;

    public FilesController(
        IFileStorageService storage,
        IFileIndexService index,
        IUploadService upload,
        IFileOperationService fileOps,
        IChunkedUploadService chunkedUpload,
        IWebSocketHandler wsHandler)
    {
        _storage = storage;
        _index = index;
        _upload = upload;
        _fileOps = fileOps;
        _chunkedUpload = chunkedUpload;
        _wsHandler = wsHandler;
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
        return Ok(result);
    }

    /// <summary>
    /// POST /api/files/upload — 上传文件（multipart）。
    /// 冲突判定与冲突副本保存在 Server.Core UploadService（与分块上传路径策略一致，F-56/T-056），
    /// 本层只透传 baseVersion 并映射 CONFLICT 错误体；上传编排（先存档旧版本→再原子覆盖→后更新索引）同样由 UploadService 保证顺序。
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

        string uploadDeviceId = HttpContext.Items["DeviceId"] as string ?? "unknown";

        await using var stream = file.OpenReadStream();

        UploadOutcome outcome;
        try
        {
            outcome = await _upload.UploadAsync(path, stream, file.Length, baseVersion, lastModified, uploadDeviceId);
        }
        catch (UploadStorageException storageEx)
        {
            return this.Error(HttpErrorCode.INTERNAL_ERROR, storageEx.Message, "服务暂时不可用，请稍后重试");
        }

        switch (outcome)
        {
            case UploadSuccessOutcome s:
                // WebSocket 广播
                await _wsHandler.BroadcastFileChangedAsync(s.Path, s.Version, uploadDeviceId);
                return Ok(new UploadResponse(new UploadData(s.Path, s.Version, s.Hash, s.Size, false)));
            case UploadConflictOutcome c:
                return this.Error(HttpErrorCode.CONFLICT,
                    $"版本冲突：客户端基于 v{c.BaseVersion}，服务端当前 v{c.CurrentVersion}",
                    "文件已被其他设备修改，请刷新后重试",
                    detail: $"currentVersion={c.CurrentVersion}, baseVersion={c.BaseVersion}, conflictPath={c.ConflictPath}");
            default:
                return this.Error(HttpErrorCode.INTERNAL_ERROR, "未知上传结果", "上传过程中出现未知错误");
        }
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

        var result = await _fileOps.DownloadAsync(path);
        if (!result.Success)
        {
            return this.Error(result.Error!.Code, result.Error.Message, result.Error.UserMessage);
        }

        Response.Headers["X-File-Hash"] = result.Entry?.CurrentHash ?? "";
        Response.Headers["X-File-Version"] = result.Entry?.Version.ToString() ?? "0";
        Response.Headers["X-File-Size"] = result.Size.ToString();
        Response.Headers["X-File-Modified"] = result.Entry?.LastModified ?? "";

        return File(result.Content!, "application/octet-stream", result.FileName);
    }
}

// ---- 请求 DTO（简单场景直接用 record，不放入共享库） ----

/// <summary>删除文件请求（BaseVersion 用于乐观并发校验，0 表示不校验）。</summary>
public record DeleteRequest(string Path, int BaseVersion = 0);

/// <summary>移动/重命名请求（BaseVersion 用于乐观并发校验，0 表示不校验）。</summary>
public record MoveRequest(string OldPath, string NewPath, int BaseVersion = 0);

/// <summary>创建目录请求。</summary>
public record MkdirRequest(string Path);
