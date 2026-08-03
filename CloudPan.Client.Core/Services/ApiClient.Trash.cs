using System.Net.Http.Json;
using System.Text.Json;
using CloudPan.Contract;

namespace CloudPan.Client.Core.Services;

/// <summary>ApiClient 部分类：回收站操作（T-014，客户端删除进回收站 + 恢复/撤销）。</summary>
public partial class ApiClient
{
    // ============================================================
    // 回收站（/api/trash，T-014：客户端删除进回收站 + 恢复/撤销）
    // ============================================================

    /// <summary>获取回收站列表（按删除时间倒序）。</summary>
    public async Task<List<TrashItem>> GetTrashAsync(CancellationToken ct = default)
    {
        var response = await _http.GetAsync(SpecRoutes.Trash, ct);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<TrashListResponse>(JsonOptions, ct);
        return result?.Data?.ToList() ?? new List<TrashItem>();
    }

    /// <summary>恢复回收站条目到原位（撤销删除）。</summary>
    public async Task RestoreTrashAsync(string metaFileName, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync(SpecRoutes.TrashRestore,
            new RestoreTrashRequestDto(metaFileName), JsonOptions, ct);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>清空回收站。</summary>
    public async Task EmptyTrashAsync(CancellationToken ct = default)
    {
        var response = await _http.DeleteAsync(SpecRoutes.TrashEmpty, ct);
        response.EnsureSuccessStatusCode();
    }
}
