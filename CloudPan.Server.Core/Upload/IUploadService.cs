namespace CloudPan.Server.Services;

/// <summary>普通上传编排结果。</summary>
public record UploadResult(string Path, int Version, string Hash, long Size);

/// <summary>
/// 普通上传编排服务。封装『先存档旧版本→再原子覆盖目标→后更新索引』的不变量，
/// 保证版本历史存档的是上传前真实内容（F-01 顺序缺陷的修复载体）。
/// </summary>
public interface IUploadService
{
    /// <summary>
    /// 执行一次普通上传编排：先存档旧版本、再原子覆盖目标文件、后更新索引与版本记录。
    /// </summary>
    /// <param name="path">目标文件路径（以 / 开头）。</param>
    /// <param name="content">上传文件内容流。</param>
    /// <param name="contentLength">上传内容字节数。</param>
    /// <param name="lastModified">客户端声明的最后修改时间（ISO 8601）。</param>
    /// <param name="deviceId">上传来源设备 ID。</param>
    /// <param name="ct">取消令牌。</param>
    Task<UploadResult> UploadAsync(
        string path, Stream content, long contentLength,
        string? lastModified, string deviceId, CancellationToken ct = default);
}
