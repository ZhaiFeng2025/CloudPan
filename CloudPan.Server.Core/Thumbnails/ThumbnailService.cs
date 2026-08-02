using System.Security.Cryptography;
using System.Text;
using CloudPan.Contract;
using CloudPan.Infrastructure.Models;
using CloudPan.Infrastructure.Storage;
using SkiaSharp;

namespace CloudPan.Server.Core;

/// <inheritdoc />
public class ThumbnailService : IThumbnailService
{
    private static readonly HashSet<string> SupportedExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp"
    };

    /// <summary>缩略图并发生成并发上限——限制同时解码的图片数，避免相册并发请求拉满 CPU。</summary>
    private const int MaxConcurrentGenerations = 3;

    private readonly IFileStorageService _storage;
    private readonly IFileIndexService? _fileIndex;

    /// <summary>生成并发门：未命中缓存的生成（解码/缩放/编码）经此门限流。</summary>
    private readonly SemaphoreSlim _generationGate = new(MaxConcurrentGenerations);

    public ThumbnailService(IFileStorageService storage, IFileIndexService? fileIndex = null)
    {
        _storage = storage;
        _fileIndex = fileIndex;
    }

    /// <inheritdoc />
    public async Task<ThumbnailResult> GetThumbnailAsync(string path, int width)
    {
        // 路径安全统一防线（防止目录穿越读取 .cloudpan 元数据或任意文件）
        string? validationError = _storage.ValidatePath(path);
        if (validationError != null)
        {
            return new ThumbnailResult(false, null,
                new DomainError(HttpErrorCode.BAD_REQUEST, validationError, "无效的文件路径"));
        }

        if (!_storage.Exists(path))
        {
            return new ThumbnailResult(false, null,
                new DomainError(HttpErrorCode.NOT_FOUND, $"文件不存在: {path}", "文件不存在，无法生成缩略图"));
        }

        string ext = Path.GetExtension(path).ToLowerInvariant();

        // 非图片类型：不支持生成缩略图（禁止 PhysicalFile 回退返回原文件，避免任意文件读取）
        if (!SupportedExts.Contains(ext))
        {
            return new ThumbnailResult(false, null,
                new DomainError(HttpErrorCode.NOT_FOUND, $"不支持的文件类型: {ext}", "该文件不是支持的图片类型"));
        }

        string thumbPath = await GetThumbCachePathAsync(path, width);

        // 缓存命中：不进并发门，避免占用生成额度
        if (File.Exists(thumbPath))
        {
            return new ThumbnailResult(true, thumbPath);
        }

        // 受限并发生成：解码/缩放为 CPU 密集操作，限制并发数避免相册拉满 CPU
        await _generationGate.WaitAsync();
        try
        {
            // 双检：等待并发门期间该缩略图可能已被其他请求生成
            if (File.Exists(thumbPath))
            {
                return new ThumbnailResult(true, thumbPath);
            }

            return GenerateThumbnail(path, width, thumbPath);
        }
        finally
        {
            _generationGate.Release();
        }
    }

    /// <summary>解码/缩放/编码并写缓存。解码失败或文件竞态删除返回错误，不回退原文件。</summary>
    private ThumbnailResult GenerateThumbnail(string path, int width, string thumbPath)
    {
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

                    // 原子写缓存：先写 .tmp 再 rename，并发生成同一缩略图时互不破坏（对齐项目原子写约定）
                    string tmpPath = thumbPath + ".tmp";
                    using (var fs = File.Create(tmpPath))
                    {
                        data.SaveTo(fs);
                    }

                    File.Move(tmpPath, thumbPath, overwrite: true);
                    return new ThumbnailResult(true, thumbPath);
                }
            }

            // 图片解码失败：返回错误，不回退原文件
            return new ThumbnailResult(false, null,
                new DomainError(HttpErrorCode.NOT_FOUND, "无法生成缩略图", "图片无法解码，无法生成缩略图"));
        }
        catch
        {
            return new ThumbnailResult(false, null,
                new DomainError(HttpErrorCode.INTERNAL_ERROR, "缩略图生成失败", "缩略图生成失败，请稍后重试"));
        }
    }

    /// <summary>
    /// 计算缩略图缓存路径。缓存 key = SHA-256(路径|宽度|索引版本|索引hash|磁盘长度|最后写入刻度) 前 16 hex。
    /// 文件更新（索引版本/hash 变化或磁盘内容变化）后 key 变化，旧缩略图自动失效。
    /// </summary>
    private async Task<string> GetThumbCachePathAsync(string filePath, int width)
    {
        // 索引为准的 version/hash：文件经同步/上传更新会提升版本并更新哈希，key 随之变化
        FileEntry? entry = _fileIndex == null ? null : await _fileIndex.GetByPathAsync(filePath);
        int version = entry?.Version ?? 0;
        string hash = entry?.CurrentHash ?? "";

        // 磁盘元数据指纹（长度 + 最后写入刻度）：未入索引的文件内容更新后同样使旧缓存失效
        string absPath = _storage.GetAbsolutePath(filePath);
        long length = 0;
        long lastWriteTicks = 0;
        try
        {
            var fi = new FileInfo(absPath);
            if (fi.Exists)
            {
                length = fi.Length;
                lastWriteTicks = fi.LastWriteTimeUtc.Ticks;
            }
        }
        catch
        {
            // 竞态：文件可能刚被删除，交由后续解码失败路径返回错误
        }

        string keyMaterial = $"{filePath}|w={width}|v={version}|h={hash}|m={length}:{lastWriteTicks}";
        string hashHex = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(keyMaterial)))[..16];
        string thumbDir = Path.Combine(
            Path.GetDirectoryName(absPath)!,
            ".cloudpan", ".thumbnails");
        Directory.CreateDirectory(thumbDir);
        return Path.Combine(thumbDir, $"{hashHex}.jpg");
    }
}
