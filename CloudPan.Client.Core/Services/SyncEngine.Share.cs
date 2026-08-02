using CloudPan.Contract;
using Microsoft.Extensions.Logging;

namespace CloudPan.Client.Core.Services;

/// <summary>
/// SyncEngine 部分实现：分享链接（T-018）——转发 IApiClient 的创建/撤销调用。
/// 分享与版本历史服务端端点已就绪（T-002 下沉领域服务），本层只做 UI 与 HTTP 客户端之间的转发与容错。
/// </summary>
public partial class SyncEngine
{
    /// <summary>创建分享链接。失败返回 null。</summary>
    public async Task<ShareCreateResponse?> CreateShareAsync(
        string filePath, string? password, string? expiresAt, int? maxDownloads, CancellationToken ct = default)
    {
        try
        {
            return await _api.CreateShareAsync(filePath, password, expiresAt, maxDownloads, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "创建分享链接失败: {Path}", filePath);
            return null;
        }
    }

    /// <summary>撤销分享链接。返回 false 表示分享不存在或已失效，或请求失败。</summary>
    public async Task<bool> RevokeShareAsync(string shareId, CancellationToken ct = default)
    {
        try
        {
            return await _api.RevokeShareAsync(shareId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "撤销分享失败: {ShareId}", shareId);
            return false;
        }
    }
}
