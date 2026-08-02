using CloudPan.Contract;
using Microsoft.Extensions.Logging;

namespace CloudPan.Client.Core.Services;

/// <summary>
/// SyncEngine 部分实现：版本历史（T-018）——转发 IApiClient 的列表/回滚调用。
/// 回滚成功后服务端广播 WS file_changed，本设备增量同步据此重新下载回滚后的内容。
/// </summary>
public partial class SyncEngine
{
    /// <summary>获取文件历史版本列表（按版本倒序）。失败返回空列表。</summary>
    public async Task<List<VersionItem>> GetVersionHistoryAsync(string path, int limit = 50, CancellationToken ct = default)
    {
        try
        {
            return await _api.GetVersionsAsync(path, limit, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "获取版本历史失败: {Path}", path);
            return new List<VersionItem>();
        }
    }

    /// <summary>回滚文件到指定历史版本。失败返回 null。</summary>
    public async Task<VersionRestoreResponse?> RestoreVersionAsync(string filePath, int version, CancellationToken ct = default)
    {
        try
        {
            return await _api.RestoreVersionAsync(filePath, version, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "回滚版本失败: {Path} v{Version}", filePath, version);
            return null;
        }
    }
}
