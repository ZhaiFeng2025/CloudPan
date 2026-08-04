namespace CloudPan.Server.Core;

/// <summary>缩略图结果。Success 时 CachePath 为已生成缓存的绝对路径。</summary>
public sealed record ThumbnailResult(bool Success, string? CachePath, DomainError? Error = null);

/// <summary>
/// 图片缩略图领域服务。封装路径校验、图片类型判定、解码缩放（SkiaSharp + 系统 WIC 回退）与缓存写盘，
/// 使 Controller 只做 HTTP 适配（F-02 下沉载体）。
/// </summary>
public interface IThumbnailService
{
    /// <summary>获取指定路径图片的缩略图缓存路径；未命中则生成并写盘。非图片/解码失败返回错误。</summary>
    Task<ThumbnailResult> GetThumbnailAsync(string path, int width);

    /// <summary>回收过期缩略图缓存：删除最后写入早于 cutoff 的 .thumbnails 缓存文件（重建成本低，重建时自动按内容指纹重新生成）。返回清理文件数。</summary>
    Task<int> ReclaimExpiredThumbnailsAsync(DateTime cutoff);
}
