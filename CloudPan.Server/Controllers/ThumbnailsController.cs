using Microsoft.AspNetCore.Mvc;
using CloudPan.Server.Services;

namespace CloudPan.Server.Controllers;

/// <summary>
/// 图片缩略图 API——服务端生成并缓存到 .thumbnails/ 目录。
/// </summary>
[ApiController]
[Route("api/thumbnails")]
public class ThumbnailsController : ControllerBase
{
    private readonly IFileStorageService _storage;

    public ThumbnailsController(IFileStorageService storage)
    {
        _storage = storage;
    }

    /// <summary>
    /// GET /api/thumbnails?path=...&width=200
    /// 返回缩略图（JPEG），首次生成后缓存到 .thumbnails/ 目录。
    /// Phase 0 简化：仅支持 JPEG/PNG，200px 宽度。
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetThumbnail([FromQuery] string path, [FromQuery] int width = 200)
    {
        if (string.IsNullOrWhiteSpace(path))
            return BadRequest(new { error = new { code = "BAD_REQUEST", message = "path 参数缺失" } });

        if (!_storage.Exists(path))
            return NotFound(new { error = new { code = "NOT_FOUND", message = $"文件不存在: {path}" } });

        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext != ".jpg" && ext != ".jpeg" && ext != ".png" && ext != ".gif" && ext != ".bmp")
        {
            // 非图片文件返回占位图标
            return File(Array.Empty<byte>(), "image/svg+xml");
        }

        var absPath = _storage.GetAbsolutePath(path);
        var thumbPath = GetThumbCachePath(path, width);

        // 缓存命中
        if (System.IO.File.Exists(thumbPath))
            return PhysicalFile(thumbPath, "image/jpeg");

        try
        {
            // 生成缩略图（使用 System.Drawing.Common 或 SkiaSharp）
            // Phase 0 简化：直接返回原图（不压缩），后续可集成 ImageSharp
            return PhysicalFile(absPath, "image/jpeg");
        }
        catch
        {
            return StatusCode(500, new { error = new { code = "INTERNAL_ERROR", message = "缩略图生成失败" } });
        }
    }

    private string GetThumbCachePath(string path, int width)
    {
        var hash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(path + width)))[..16];
        var thumbDir = Path.Combine(Path.GetDirectoryName(_storage.GetAbsolutePath(path))!,
            ".cloudpan", ".thumbnails");
        Directory.CreateDirectory(thumbDir);
        return Path.Combine(thumbDir, $"{hash}.jpg");
    }
}
