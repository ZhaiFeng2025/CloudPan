using CloudPan.Contract;

namespace CloudPan.Server.Core;

/// <summary>
/// 审计日志写入服务接口。
/// </summary>
public interface ISyncLogService
{
    /// <summary>
    /// 记录同步操作日志。写入失败不抛异常，仅日志警告。
    /// </summary>
    /// <param name="filePath">文件路径。</param>
    /// <param name="operation">操作类型。</param>
    /// <param name="deviceId">操作设备 ID。</param>
    /// <param name="result">操作结果。</param>
    /// <param name="details">附加信息（可选）。</param>
    Task LogAsync(string filePath, SyncOperation operation, string deviceId,
        LogResult result, string? details = null);
}
