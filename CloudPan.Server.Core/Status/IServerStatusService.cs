namespace CloudPan.Server.Services;

/// <summary>文件条目摘要（管理面板文件列表）。</summary>
public sealed record FileEntryInfo(string Path, int Type, string? CurrentHash, long CurrentSize, int Version, int State, string LastModified);

/// <summary>设备摘要。</summary>
public sealed record DeviceInfo(string Id, string Name, string? Person, string LastSeen, int Online, string RegisteredAt);

/// <summary>同步日志摘要。</summary>
public sealed record SyncLogInfo(long Id, string FilePath, int Operation, string DeviceId, int Result, string? Details, string CreatedAt);

/// <summary>服务端聚合统计。</summary>
public sealed record ServerStats(int FileCount, int DeviceCount, int OnlineDeviceCount, int LogCount);

/// <summary>
/// 服务端只读状态查询服务（管理面板/设备列表/健康检查数据）。
/// 把 Admin/Devices/Health Controller 的 DbContext 查询下沉到 Server.Core，Controller 只做 HTTP 适配（R-A3）。
/// </summary>
public interface IServerStatusService
{
    /// <summary>按路径前缀查询文件条目（管理面板文件列表）。</summary>
    Task<List<FileEntryInfo>> GetFilesAsync(string? path, int limit);

    /// <summary>按最后在线时间倒序返回设备列表。</summary>
    Task<List<DeviceInfo>> GetDevicesAsync();

    /// <summary>按时间倒序返回同步日志。</summary>
    Task<List<SyncLogInfo>> GetLogsAsync(int limit);

    /// <summary>聚合统计（文件数/设备数/在线设备数/日志数）。</summary>
    Task<ServerStats> GetStatsAsync();

    /// <summary>获取服务端证书 SHA-256 指纹（TOFU pinning）。</summary>
    Task<string?> GetCertFingerprintAsync();

    /// <summary>PRAGMA integrity_check 数据库完整性校验，返回 "ok"/"error"。</summary>
    Task<string> CheckDbIntegrityAsync();
}
