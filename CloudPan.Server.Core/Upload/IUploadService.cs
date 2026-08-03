namespace CloudPan.Server.Core;

/// <summary>上传操作结果基类（参照分块上传 ChunkUploadOutcome 模式，冲突策略单一实现于 Core）。</summary>
public abstract record UploadOutcome;

/// <summary>上传成功。</summary>
public sealed record UploadSuccessOutcome(string Path, int Version, string Hash, long Size) : UploadOutcome;

/// <summary>上传版本冲突（已保存冲突副本，语义与分块上传 ChunkConflictOutcome 一致）。</summary>
public sealed record UploadConflictOutcome(string Path, int CurrentVersion, int BaseVersion, string ConflictPath) : UploadOutcome;

/// <summary>
/// 普通上传编排服务。封装『先存档旧版本→再原子覆盖目标→后更新索引』的不变量，
/// 保证版本历史存档的是上传前真实内容（F-01 顺序缺陷的修复载体）。
/// 冲突判定与冲突副本保存（F-56/T-056）与分块上传路径策略一致，统一在 Core 内执行，Controller 只透传 baseVersion。
/// </summary>
public interface IUploadService
{
    /// <summary>
    /// 执行一次普通上传编排：先冲突检测（baseVersion > 0 且服务端当前版本更大 → 保存冲突副本并返回冲突，
    /// 不推进主文件版本），再『先存档旧版本、再原子覆盖目标文件、后更新索引与版本记录』。
    /// </summary>
    /// <param name="path">目标文件路径（以 / 开头）。</param>
    /// <param name="content">上传内容流。</param>
    /// <param name="contentLength">上传内容字节数。</param>
    /// <param name="baseVersion">客户端乐观并发基准版本（0 表示不校验）。</param>
    /// <param name="lastModified">客户端声明的最后修改时间（ISO 8601）。</param>
    /// <param name="deviceId">上传来源设备 ID。</param>
    /// <param name="ct">取消令牌。</param>
    Task<UploadOutcome> UploadAsync(
        string path, Stream content, long contentLength, int baseVersion,
        string? lastModified, string deviceId, CancellationToken ct = default);
}
