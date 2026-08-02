using CloudPan.Contract;
using CloudPan.Server.Host;
using Microsoft.AspNetCore.Mvc;

namespace CloudPan.Server.Host.Controllers;

/// <summary>
/// 文件操作 API（partial 拆分）：删除、移动、创建文件夹、搜索。
/// 领域逻辑（DB 事务 + FS + 审计日志）在 Server.Core IFileOperationService。
/// </summary>
public partial class FilesController
{

    /// <summary>
    /// POST /api/files/delete — 删除文件或文件夹（递归）。
    /// </summary>
    [HttpPost("delete")]
    public async Task<IActionResult> Delete([FromBody] DeleteRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Path))
        {
            return this.Error(HttpErrorCode.BAD_REQUEST, "path 参数缺失", "请提供文件路径");
        }

        string deviceId = HttpContext.Items["DeviceId"] as string ?? "unknown";
        var result = await _fileOps.DeleteAsync(request.Path, request.BaseVersion, deviceId);
        if (!result.Success)
        {
            return this.Error(result.Error!.Code, result.Error.Message, result.Error.UserMessage, result.Error.Detail);
        }

        // WebSocket 广播
        await _wsHandler.BroadcastFileDeletedAsync(request.Path, deviceId);

        return Ok(new
        {
            data = new { path = request.Path, deletedVersion = result.DeletedVersion }
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

        string deviceId = HttpContext.Items["DeviceId"] as string ?? "unknown";
        var result = await _fileOps.MoveAsync(request.OldPath, request.NewPath, request.BaseVersion, deviceId);
        if (!result.Success)
        {
            return this.Error(result.Error!.Code, result.Error.Message, result.Error.UserMessage);
        }

        // WebSocket 广播
        await _wsHandler.BroadcastFileRenamedAsync(request.OldPath, request.NewPath, deviceId);

        return Ok(new
        {
            data = new { oldPath = request.OldPath, newPath = request.NewPath, version = result.Version }
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

        var result = await _fileOps.MkdirAsync(request.Path);
        if (!result.Success)
        {
            return this.Error(result.Error!.Code, result.Error.Message, result.Error.UserMessage);
        }

        return Ok(new { data = new { path = result.Path } });
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
}
