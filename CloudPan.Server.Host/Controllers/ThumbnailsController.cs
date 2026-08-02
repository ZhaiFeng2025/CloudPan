using CloudPan.Server;
using CloudPan.Server.Services;
using CloudPan.Shared;
using Microsoft.AspNetCore.Mvc;
using SkiaSharp;

namespace CloudPan.Server.Controllers;

/// <summary>
/// 图片缩略图 API——服务端使用 SkiaSharp 生成并缓存到 .thumbnails/ 目录。
/// </summary>
[ApiController]
[Route("api/thumbnails")]
[EndpointAuth(AuthMode.Token)]
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
    /// </summary>
    [HttpGet]
    public IActionResult GetThumbnail([FromQuery] string path, [FromQuery] int width = 200)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return this.Error(HttpErrorCode.BAD_REQUEST, "path 参数缺失", "缺少文件路径参数");
        }

        // 路径安全校验（统一防线，防止目录穿越读取 .cloudpan 元数据或任意文件）
        string? validationError = _storage.ValidatePath(path);
        if (validationError != null)
        {
            return this.Error(HttpErrorCode.BAD_REQUEST, validationError, "无效的文件路径");
        }

        if (!_storage.Exists(path))
        {
            return this.Error(HttpErrorCode.NOT_FOUND, $"文件不存在: {path}", "文件不存在，无法生成缩略图");
        }

        string ext = Path.GetExtension(path).ToLowerInvariant();

        // 非图片类型：不支持生成缩略图（禁止 PhysicalFile 回退返回原文件，避免任意文件读取）
        if (ext is not (".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".webp"))
        {
            return this.Error(HttpErrorCode.NOT_FOUND, $"不支持的文件类型: {ext}", "该文件不是支持的图片类型");
        }

        string thumbPath = GetThumbCachePath(path, width);

        // 缓存命中
        if (System.IO.File.Exists(thumbPath))
        {
            return PhysicalFile(thumbPath, "image/jpeg");
        }

        // 尝试生成缩略图
        try
        {
            if (ext is ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".webp")
            {
                using SKBitmap input = SKBitmap.Decode(_storage.GetAbsolutePath(path));
                if (input != null)
                {
                    float ratio = (float)width / input.Width;
                    int height = (int)(input.Height * ratio);
                    int clampedW = Math.Min(width, input.Width);
                    int clampedH = Math.Min(Math.Max(height, 1), input.Height);

                    using var resized = input.Resize(new SKImageInfo(clampedW, clampedH), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
                    if (resized != null)
                    {
                        using SKImage image = SKImage.FromBitmap(resized);
                        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 80);
                        string? dir = Path.GetDirectoryName(thumbPath);
                        if (dir != null)
                        {
                            Directory.CreateDirectory(dir);
                        }

                        using var fs = System.IO.File.Create(thumbPath);
                        data.SaveTo(fs);
                        return PhysicalFile(thumbPath, "image/jpeg");
                    }
                }
            }

            // 图片解码失败：返回错误，不回退原文件
            return this.Error(HttpErrorCode.NOT_FOUND, "无法生成缩略图", "图片无法解码，无法生成缩略图");
        }
        catch
        {
            return this.Error(HttpErrorCode.INTERNAL_ERROR, "缩略图生成失败", "缩略图生成失败，请稍后重试");
        }
    }

    private string GetThumbCachePath(string filePath, int width)
    {
        string hash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(filePath + "|w=" + width)))[..16];
        string absPath = _storage.GetAbsolutePath(filePath);
        string thumbDir = Path.Combine(
            Path.GetDirectoryName(absPath)!,
            ".cloudpan", ".thumbnails");
        Directory.CreateDirectory(thumbDir);
        return Path.Combine(thumbDir, $"{hash}.jpg");
    }
}
