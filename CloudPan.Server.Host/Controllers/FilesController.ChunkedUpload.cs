using CloudPan.Contract;
using CloudPan.Server.Core;
using CloudPan.Server.Host;
using Microsoft.AspNetCore.Mvc;

namespace CloudPan.Server.Host.Controllers;

/// <summary>
/// 文件操作 API（partial 拆分）：分块上传。
/// 会话管理/块写入/合并校验/冲突处理在 Server.Core IChunkedUploadService，本类只做参数绑定与状态码适配。
/// </summary>
public partial class FilesController
{

    // ============================================================
    // 分块上传
    // ============================================================

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
        // 1. 参数校验（纯绑定层）
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

        string deviceId = HttpContext.Items["DeviceId"] as string ?? "unknown";

        await using var chunkStream = chunk.OpenReadStream();
        var outcome = await _chunkedUpload.ReceiveChunkAsync(
            path, chunkIndex, totalChunks, fileHash, baseVersion, lastModified, deviceId, chunkStream);

        return outcome switch
        {
            ChunkProgressOutcome p => Ok(new
            {
                data = new
                {
                    path = p.Path,
                    chunkIndex = p.ChunkIndex,
                    receivedCount = p.ReceivedCount,
                    totalChunks = p.TotalChunks,
                    isComplete = p.IsComplete
                }
            }),
            ChunkCompletedOutcome c => Ok(new
            {
                data = new
                {
                    path = c.Path,
                    version = c.Version,
                    hash = c.Hash,
                    size = c.Size,
                    status = "complete"
                }
            }),
            ChunkConflictOutcome c => this.Error(HttpErrorCode.CONFLICT,
                $"版本冲突：客户端基于 v{c.BaseVersion}，服务端当前 v{c.CurrentVersion}",
                "文件已被其他设备修改，请刷新后重试",
                detail: $"currentVersion={c.CurrentVersion}, baseVersion={c.BaseVersion}, conflictPath={c.ConflictPath}"),
            ChunkErrorOutcome e => this.Error(e.Error.Code, e.Error.Message, e.Error.UserMessage, e.Error.Detail),
            _ => this.Error(HttpErrorCode.INTERNAL_ERROR, "未知错误", "上传过程中出现未知错误")
        };
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

        var status = await _chunkedUpload.GetStatusAsync(path);

        if (!status.Found)
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

        return Ok(new
        {
            data = new
            {
                status.FilePath,
                receivedChunks = status.ReceivedChunks,
                status.TotalChunks,
                isComplete = status.IsComplete,
                status.DeviceId,
                status.CreatedAt
            }
        });
    }
}
