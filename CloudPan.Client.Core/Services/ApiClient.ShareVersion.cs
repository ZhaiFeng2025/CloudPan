using System.Net.Http.Json;
using System.Text.Json;
using CloudPan.Contract;

namespace CloudPan.Client.Core.Services;

/// <summary>ApiClient 部分类：分享链接与版本历史操作（T-018）。</summary>
public partial class ApiClient
{
    // ============================================================
    // 分享与版本历史（/api/shares + /api/versions，T-018：客户端 UI 入口）
    // ============================================================

    /// <summary>创建分享链接。expiresAt 传 ISO 8601 UTC（如 DateTime.UtcNow.AddDays(7).ToString("O")），null 表示永不过期。</summary>
    public async Task<ShareCreateResponse?> CreateShareAsync(
        string filePath, string? password, string? expiresAt, int? maxDownloads, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync(SpecRoutes.Shares,
            new { filePath, password, expiresAt, maxDownloads }, JsonOptions, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ShareCreateResponse>(JsonOptions, ct);
    }

    /// <summary>撤销分享链接。返回 false 表示分享不存在或已失效。</summary>
    public async Task<bool> RevokeShareAsync(string shareId, CancellationToken ct = default)
    {
        var response = await _http.DeleteAsync(
            SpecRoutes.SharesByShareId.Replace("{shareId}", Uri.EscapeDataString(shareId)), ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }

        response.EnsureSuccessStatusCode();
        return true;
    }

    /// <summary>获取文件历史版本列表（按版本倒序，上限 limit）。</summary>
    public async Task<List<VersionItem>> GetVersionsAsync(string path, int limit = 50, CancellationToken ct = default)
    {
        var response = await _http.GetAsync(
            $"{SpecRoutes.Versions}?path={Uri.EscapeDataString(path)}&limit={limit}", ct);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<VersionListResponse>(JsonOptions, ct);
        return result?.Data?.ToList() ?? new List<VersionItem>();
    }

    /// <summary>回滚文件到指定历史版本（服务端会先存档当前版本，再用历史文件覆盖）。</summary>
    public async Task<VersionRestoreResponse?> RestoreVersionAsync(string filePath, int version, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync(SpecRoutes.VersionsRestore,
            new { filePath, version }, JsonOptions, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<VersionRestoreResponse>(JsonOptions, ct);
    }
}
