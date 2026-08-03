using CloudPan.Contract;

namespace CloudPan.Server.Core;

/// <summary>
/// 服务端只读状态查询服务（管理面板/设备列表/健康检查数据）。
/// 把 Admin/Devices/Health Controller 的 DbContext 查询下沉到 Server.Core，Controller 只做 HTTP 适配（R-A3）。
/// 响应类型复用契约生成的 ApiResponses 记录（AdminFileItem/AdminDeviceItem/AdminLogItem/AdminStatsResponse），响应体单一事实来源。
/// </summary>
public interface IServerStatusService
{
    /// <summary>按路径前缀查询文件条目（管理面板文件列表）。</summary>
    Task<List<AdminFileItem>> GetFilesAsync(string? path, int limit);

    /// <summary>按最后在线时间倒序返回设备列表（管理面板形状，id 字段）。</summary>
    Task<List<AdminDeviceItem>> GetDevicesAsync();

    /// <summary>按时间倒序返回同步日志。</summary>
    Task<List<AdminLogItem>> GetLogsAsync(int limit);

    /// <summary>聚合统计（文件数/设备数/在线设备数/日志数）。</summary>
    Task<AdminStatsResponse> GetStatsAsync();

    /// <summary>获取服务端证书 SHA-256 指纹（TOFU pinning）。</summary>
    Task<string?> GetCertFingerprintAsync();

    /// <summary>PRAGMA integrity_check 数据库完整性校验，返回 "ok"/"error"。</summary>
    Task<string> CheckDbIntegrityAsync();
}
