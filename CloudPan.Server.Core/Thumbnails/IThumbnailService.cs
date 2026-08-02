namespace CloudPan.Server.Services;

/// <summary>缩略图结果。Success 时 CachePath 为已生成缓存的绝对路径。</summary>
public sealed record ThumbnailResult(bool Success, string? CachePath, DomainError? Error = null);

/// <summary>
/// 图片缩略图领域服务。封装路径校验、图片类型判定、SkiaSharp 解码缩放与缓存写盘，
/// 使 Controller 只做 HTTP 适配（F-02 下沉载体）。
/// </summary>
public interface IThumbnailService
{
    /// <summary>获取指定路径图片的缩略图缓存路径；未命中则生成并写盘。非图片/解码失败返回错误。</summary>
    Task<ThumbnailResult> GetThumbnailAsync(string path, int width);
}
