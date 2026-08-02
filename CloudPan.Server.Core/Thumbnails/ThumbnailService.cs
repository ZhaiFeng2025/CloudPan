using System.Security.Cryptography;
using System.Text;
using CloudPan.Shared;
using SkiaSharp;

namespace CloudPan.Server.Services;

/// <inheritdoc />
public class ThumbnailService : IThumbnailService
{
    private static readonly HashSet<string> SupportedExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp"
    };

    private readonly IFileStorageService _storage;

    public ThumbnailService(IFileStorageService storage)
    {
        _storage = storage;
    }

    /// <inheritdoc />
    public Task<ThumbnailResult> GetThumbnailAsync(string path, int width)
    {
        // 路径安全统一防线（防止目录穿越读取 .cloudpan 元数据或任意文件）
        string? validationError = _storage.ValidatePath(path);
        if (validationError != null)
        {
            return Task.FromResult(new ThumbnailResult(false, null,
                new DomainError(HttpErrorCode.BAD_REQUEST, validationError, "无效的文件路径")));
        }

        if (!_storage.Exists(path))
        {
            return Task.FromResult(new ThumbnailResult(false, null,
                new DomainError(HttpErrorCode.NOT_FOUND, $"文件不存在: {path}", "文件不存在，无法生成缩略图")));
        }

        string ext = Path.GetExtension(path).ToLowerInvariant();

        // 非图片类型：不支持生成缩略图（禁止 PhysicalFile 回退返回原文件，避免任意文件读取）
        if (!SupportedExts.Contains(ext))
        {
            return Task.FromResult(new ThumbnailResult(false, null,
                new DomainError(HttpErrorCode.NOT_FOUND, $"不支持的文件类型: {ext}", "该文件不是支持的图片类型")));
        }

        string thumbPath = GetThumbCachePath(path, width);

        // 缓存命中
        if (File.Exists(thumbPath))
        {
            return Task.FromResult(new ThumbnailResult(true, thumbPath));
        }

        // 尝试生成缩略图
        try
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

                    using var fs = File.Create(thumbPath);
                    data.SaveTo(fs);
                    return Task.FromResult(new ThumbnailResult(true, thumbPath));
                }
            }

            // 图片解码失败：返回错误，不回退原文件
            return Task.FromResult(new ThumbnailResult(false, null,
                new DomainError(HttpErrorCode.NOT_FOUND, "无法生成缩略图", "图片无法解码，无法生成缩略图")));
        }
        catch
        {
            return Task.FromResult(new ThumbnailResult(false, null,
                new DomainError(HttpErrorCode.INTERNAL_ERROR, "缩略图生成失败", "缩略图生成失败，请稍后重试")));
        }
    }

    /// <summary>计算缩略图缓存路径（key 为 path + width 的 SHA-256 前 16 hex）。</summary>
    private string GetThumbCachePath(string filePath, int width)
    {
        string hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(filePath + "|w=" + width)))[..16];
        string absPath = _storage.GetAbsolutePath(filePath);
        string thumbDir = Path.Combine(
            Path.GetDirectoryName(absPath)!,
            ".cloudpan", ".thumbnails");
        Directory.CreateDirectory(thumbDir);
        return Path.Combine(thumbDir, $"{hash}.jpg");
    }
}
