using CloudPan.Contract;
using CloudPan.Infrastructure.Persistence.Client;
using Microsoft.Extensions.Logging;

namespace CloudPan.Client.Core.Services;

/// <summary>
/// T-094/F-136：恢复目标已被同名重建（CONFLICT）时的处理方式。
/// Overwrite=同名目标移入回收站后重试恢复（被恢复内容胜出，旧目标可回收站找回）；
/// RenameTarget=同名目标改名释放原路径后重试恢复（两文件并存）。
/// </summary>
public enum RestoreConflictMode
{
    Overwrite,
    RenameTarget,
}

/// <summary>
/// 同步引擎管理操作服务（T-070 拆分）：回收站（T-014）、分享（T-018）、版本历史（T-018）。
/// 只读/管理侧操作，不触碰同步状态机的可变状态（计数器/事件/锁），依赖注入 ApiClient/DbContextFactory。
/// </summary>
internal sealed class SyncManageService
{
    private readonly IApiClient _api;
    private readonly IClientStoreFactory _storeFactory;
    private readonly ILogger<SyncEngine> _logger;
    private readonly string _syncRoot;

    public SyncManageService(
        IApiClient api,
        IClientStoreFactory storeFactory,
        ILogger<SyncEngine> logger,
        string syncRoot)
    {
        _api = api;
        _storeFactory = storeFactory;
        _logger = logger;
        _syncRoot = syncRoot;
    }

    /// <summary>获取回收站条目列表（按删除时间倒序）。</summary>
    public async Task<List<TrashItem>> GetTrashAsync(CancellationToken ct = default)
    {
        try
        {
            return await _api.GetTrashAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "获取回收站列表失败");
            return new List<TrashItem>();
        }
    }

    /// <summary>恢复回收站条目到原位（撤销删除）。恢复后服务端重建索引并提升版本，客户端增量同步据此重新下载。</summary>
    public async Task<bool> RestoreTrashAsync(TrashItem item, CancellationToken ct = default)
    {
        try
        {
            // 回收站元数据文件名 = 条目 TrashFileName + ".json"（对齐 TrashService.MoveToTrashAsync 写盘命名）
            await _api.RestoreTrashAsync(item.TrashFileName + ".json", ct);
            return true;
        }
        catch (System.Net.Http.HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            // T-078/F-120：恢复目标已存在（服务端 409 CONFLICT，如删除后同路径重建/其他设备占用）——
            // 不再吞成裸 false 显示泛化『恢复失败』，抛白话可操作归因（对齐服务端 friendlyMessage），
            // UI 现有 catch 直接展示：目标位置已有文件，请先处理或改名（覆盖/改名/取消由用户据此决策）。
            _logger.LogWarning(ex, "恢复回收站失败——目标位置已有文件: {Path}", item.OriginalPath);
            throw new System.Net.Http.HttpRequestException("目标位置已有文件，请先处理或改名",
                ex, System.Net.HttpStatusCode.Conflict);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "恢复回收站失败: {Path}", item.OriginalPath);
            return false;
        }
    }

    /// <summary>
    /// T-094/F-136：恢复回收站条目时目标已被同名重建（服务端 409 CONFLICT）的可操作收敛——
    /// 按用户选择处理同名目标后重试恢复，一键解决冲突，避免『点了没反应』死端。
    /// 返回 true=恢复成功；false=目标已变化/处理失败（不抛异常，UI 据此提示）。
    /// </summary>
    public async Task<bool> RestoreTrashResolveAsync(TrashItem item, RestoreConflictMode mode, CancellationToken ct = default)
    {
        try
        {
            // 同名目标可能已被处理，先直接重试一次
            try
            {
                await _api.RestoreTrashAsync(item.TrashFileName + ".json", ct);
                return true;
            }
            catch (System.Net.Http.HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Conflict)
            {
                _logger.LogInformation("恢复冲突：目标已存在，按 {Mode} 收敛: {Path}", mode, item.OriginalPath);
            }

            // 处理同名目标，释放原路径
            if (mode == RestoreConflictMode.Overwrite)
            {
                // 覆盖：同名目标移入回收站（服务端软删 + 本地副本删除；失败则不再继续）
                await DeleteForTrashAsync(item.OriginalPath, ct);
            }
            else
            {
                // 改名：同名目标追加「（恢复冲突）」后缀释放原路径，两文件并存。
                // baseVersion 取本地快照版本，无快照（目标未同步过）时用 0 = 不校验（对齐 T-089 语义）。
                await using var store = await _storeFactory.CreateStoreAsync(ct);
                int version = (await store.GetSnapshotAsync(item.OriginalPath, ct))?.Version ?? 0;
                await _api.MoveAsync(item.OriginalPath, RestoreConflictNewPath(item.OriginalPath), version, ct);
                // 本地快照/副本跟随由增量同步按版本对账（服务端版本递增触发变更检测），此处不手工改快照。
            }

            // 重试恢复
            await _api.RestoreTrashAsync(item.TrashFileName + ".json", ct);
            return true;
        }
        catch (System.Net.Http.HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            _logger.LogWarning(ex, "恢复冲突收敛后仍冲突（目标已变化）: {Path}", item.OriginalPath);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "恢复冲突收敛失败: {Path}", item.OriginalPath);
            return false;
        }
    }

    /// <summary>恢复冲突改名目标的新路径：同名目标追加「（恢复冲突）」后缀释放原路径。</summary>
    private static string RestoreConflictNewPath(string originalPath)
    {
        string p = originalPath.TrimEnd('/');
        string dir = p.Contains('/') ? p[..p.LastIndexOf('/')] : "";
        string name = p[(p.LastIndexOf('/') + 1)..];
        int dot = name.LastIndexOf('.');
        string baseName = dot > 0 ? name[..dot] : name;
        string ext = dot > 0 ? name[dot..] : "";
        string newName = $"{baseName}（恢复冲突）{ext}";
        return dir.Length == 0 ? $"/{newName}" : $"{dir}/{newName}";
    }

    /// <summary>清空回收站。</summary>
    public async Task<bool> EmptyTrashAsync(CancellationToken ct = default)
    {
        try
        {
            await _api.EmptyTrashAsync(ct);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "清空回收站失败");
            return false;
        }
    }

    /// <summary>
    /// 删除文件浏览视图中的文件/目录（T-014，默认进回收站）：
    /// 有服务端记录 → 调 /api/files/delete（软删墓碑传播 + 移入回收站，T-005 已下沉），并清快照；
    /// 本地副本即时删除。返回可撤销的回收站条目（供 5 秒内撤销）；本地仅存文件（无服务端记录）直接删本地，返回 null（=成功，无可撤销项）。
    /// T-115：服务端删除失败时抛出异常（删除未生效，本地副本保留），由调用方计入失败——
    /// null 不再含『失败』语义，仅表示『删除成功但无回收站撤销条目』，消除双语义。
    /// </summary>
    public async Task<TrashItem?> DeleteForTrashAsync(string path, CancellationToken ct = default)
    {
        await using var store = await _storeFactory.CreateStoreAsync(ct);
        var snapshot = await store.GetSnapshotAsync(path);

        // 1. 有服务端记录 → 先调服务端删除（进回收站 + 墓碑传播），失败则抛异常（删除未生效，本地副本保留）
        if (snapshot != null)
        {
            try
            {
                await _api.DeleteAsync(path, snapshot.Version, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "服务端删除失败，本地副本保留: {Path}", path);
                throw;
            }

            // 清快照（目录删除时子路径快照一并清除，避免后续扫描重复删除）。
            // 内存过滤：EF Core 无法将 StartsWith(StringComparison) 翻译到 SQLite（与 GetFileBrowserAsync 全量加载快照的模式一致）。
            string prefix = path.EndsWith('/') ? path : path + "/";
            var snapshots = await store.GetAllSnapshotsAsync(ct);
            var toRemove = snapshots
                .Where(s => string.Equals(s.Path, path, StringComparison.OrdinalIgnoreCase)
                         || s.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (toRemove.Count > 0)
            {
                store.RemoveSnapshots(toRemove);
                await store.CommitAsync();
            }
        }

        // 2. 本地副本即时删除（浏览视图立即消失；其他设备由墓碑传播删本地副本）
        try
        {
            // T-085：SyncPath.ToLocalPath 拒绝越界相对路径（抛 ArgumentException），本地删除同样不得越界
            string localPath = SyncPath.ToLocalPath(_syncRoot, path);
            if (Directory.Exists(localPath))
            {
                Directory.Delete(localPath, recursive: true);
            }
            else if (File.Exists(localPath))
            {
                SyncPath.SafeDelete(localPath, _logger);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "删除本地副本失败: {Path}", path);
        }

        if (snapshot == null)
        {
            return null; // 本地仅存文件：无服务端记录，无从回收站撤销（=成功，非失败）
        }

        // 3. 查回收站条目供撤销（撤销 = 恢复）
        try
        {
            var items = await _api.GetTrashAsync(ct);
            return items.FirstOrDefault(t => string.Equals(t.OriginalPath, path, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "查询回收站条目失败（不影响删除）: {Path}", path);
            return null;
        }
    }

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

    /// <summary>获取当前设备创建的分享链接列表（管理入口展示/撤销，T-112）。失败返回空列表。</summary>
    public async Task<List<ShareListItem>> GetSharesAsync(CancellationToken ct = default)
    {
        try
        {
            return await _api.GetSharesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "获取分享列表失败");
            return new List<ShareListItem>();
        }
    }

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
