namespace CloudPan.Server.Core;

/// <summary>历史版本记录信息（列表展示）。</summary>
public sealed record VersionRecordInfo(int Version, string Hash, long Size, string Timestamp, string DeviceId, int? RestoredFromVersion);

/// <summary>版本回滚结果。</summary>
public sealed record VersionRestoreResult(
    bool Success,
    string? Path,
    int? Version,
    string? Hash,
    long? Size,
    int? RestoredFromVersion,
    DomainError? Error = null);

/// <summary>
/// 版本历史领域服务。封装历史版本列表与回滚（DB 事务 + FS 存档/覆盖 + 索引更新 + 审计日志），
/// 使 Controller 只做 HTTP 适配（F-02 下沉载体）。与 IVersionService（全局版本号分配）职责分离。
/// </summary>
public interface IVersionHistoryService
{
    /// <summary>获取文件的历史版本列表（按版本倒序，上限 limit，单次最多 50 条）。</summary>
    Task<List<VersionRecordInfo>> GetVersionsAsync(string path, int limit);

    /// <summary>回滚文件到指定历史版本：先存档当前版本、再用历史文件原子覆盖目标、更新索引。</summary>
    Task<VersionRestoreResult> RestoreAsync(string filePath, int version, string deviceId);
}
