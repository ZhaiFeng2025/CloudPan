using CloudPan.Server;
using CloudPan.Server.Services;
using CloudPan.Shared;
using Microsoft.AspNetCore.Mvc;

namespace CloudPan.Server.Controllers;

/// <summary>
/// 图片缩略图 API——只做参数绑定与状态码适配，领域逻辑（校验/解码/缩放/缓存写盘）在 Server.Core IThumbnailService。
/// </summary>
[ApiController]
[Route("api/thumbnails")]
[EndpointAuth(AuthMode.Token)]
public class ThumbnailsController : ControllerBase
{
    private readonly IThumbnailService _thumbnails;

    public ThumbnailsController(IThumbnailService thumbnails)
    {
        _thumbnails = thumbnails;
    }

    /// <summary>
    /// GET /api/thumbnails?path=...&width=200
    /// 返回缩略图（JPEG），首次生成后缓存到 .thumbnails/ 目录。
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetThumbnail([FromQuery] string path, [FromQuery] int width = 200)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return this.Error(HttpErrorCode.BAD_REQUEST, "path 参数缺失", "缺少文件路径参数");
        }

        var result = await _thumbnails.GetThumbnailAsync(path, width);
        if (!result.Success)
        {
            return this.Error(result.Error!.Code, result.Error.Message, result.Error.UserMessage);
        }

        return PhysicalFile(result.CachePath!, "image/jpeg");
    }
}
