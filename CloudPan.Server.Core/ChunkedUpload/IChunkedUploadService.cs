namespace CloudPan.Server.Services;

/// <summary>分块上传操作结果基类。</summary>
public abstract record ChunkUploadOutcome;

/// <summary>分块已接收（含幂等跳过），尚未合并完成。</summary>
public sealed record ChunkProgressOutcome(string Path, int ChunkIndex, int ReceivedCount, int TotalChunks, bool IsComplete) : ChunkUploadOutcome;

/// <summary>全部分块到达并合并完成。</summary>
public sealed record ChunkCompletedOutcome(string Path, int Version, string Hash, long Size) : ChunkUploadOutcome;

/// <summary>分块上传版本冲突（已保存冲突副本）。</summary>
public sealed record ChunkConflictOutcome(string Path, int CurrentVersion, int BaseVersion, string ConflictPath) : ChunkUploadOutcome;

/// <summary>分块上传错误。</summary>
public sealed record ChunkErrorOutcome(DomainError Error) : ChunkUploadOutcome;

/// <summary>分块上传进度查询结果。Found=false 表示无进行中的会话。</summary>
public sealed record ChunkStatusResult(
    bool Found, string? FilePath, IReadOnlyList<int>? ReceivedChunks, int TotalChunks, bool IsComplete, string? DeviceId, string? CreatedAt);

/// <summary>
/// 分块上传领域服务。封装分块会话管理、块写入、位图更新、合并校验与 Finalize
/// （存档旧版本 + DB 事务 + 原子覆盖 + 审计日志），使 Controller 只做参数绑定与状态码适配（F-02 下沉载体）。
/// </summary>
public interface IChunkedUploadService
{
    /// <summary>接收一个分块：管理会话、追加块数据、更新位图；全部分块到达时执行合并。</summary>
    Task<ChunkUploadOutcome> ReceiveChunkAsync(
        string path, int chunkIndex, int totalChunks, string fileHash,
        int baseVersion, string? lastModified, string deviceId, Stream chunkContent);

    /// <summary>查询分块上传进度。</summary>
    Task<ChunkStatusResult> GetStatusAsync(string path);
}
