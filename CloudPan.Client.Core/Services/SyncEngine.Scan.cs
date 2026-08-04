using CloudPan.Contract;
using CloudPan.Infrastructure.Models;
using CloudPan.Infrastructure.Persistence.Client;
using Microsoft.Extensions.Logging;

namespace CloudPan.Client.Core.Services;

/// <summary>SyncEngine 部分实现：全量/增量同步拉取与游标推进（远程变更应用已下沉 SyncRemoteApplier）。</summary>
public partial class SyncEngine
{
    // ============================================================
    // 同步核心
    // ============================================================

    private async Task FullSyncAsync(CancellationToken ct)
    {
        NotifyStatus("首次同步 — 下载远程文件...");
        _logger.LogInformation("开始全量同步");

        // 检查磁盘空间（低于 100MB 拒绝同步）
        try
        {
            DriveInfo drive = new DriveInfo(Path.GetPathRoot(_syncRoot)!);
            if (drive.AvailableFreeSpace < 100_000_000)
            {
                _logger.LogError("磁盘空间不足: {Available}MB，同步已暂停", drive.AvailableFreeSpace / 1_048_576);
                NotifyStatus("同步失败—磁盘空间不足 (可用 " + (drive.AvailableFreeSpace / 1_048_576) + " MB)");
                return;
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "获取磁盘信息失败"); }

        await using var store = await _storeFactory.CreateStoreAsync();
        var cursor = await store.GetCursorAsync();
        int sinceVersion = cursor?.LastMaxVersion ?? 0;
        int maxVersion = sinceVersion;

        // 分页循环拉取全量文件树
        string? nextCursor = null;
        int processedCount = 0;
        do
        {
            var response = await _api.GetFileTreeAsync(sinceVersion, cursor: nextCursor, ct: ct);
            if (response == null)
            {
                break;
            }

            await _remoteApplier.ApplyRemoteChangesAsync(store, response, ct);
            processedCount += response.Data.Length;
            NotifyStatus($"首次同步 — 下载远程文件 ({processedCount} 项)");
            nextCursor = response.HasMore ? response.NextCursor : null;
            if (response.MaxVersion > maxVersion)
            {
                maxVersion = response.MaxVersion;
            }
        }
        while (nextCursor != null && !ct.IsCancellationRequested);

        // 更新游标（使用拉取开始前的版本号，确保正确性）
        if (cursor == null)
        {
            store.AddCursor(new SyncCursor { Id = 1, LastMaxVersion = maxVersion, LastSyncAt = DateTime.UtcNow.ToString("O") });
        }
        else
        {
            cursor.LastMaxVersion = maxVersion;
            cursor.LastSyncAt = DateTime.UtcNow.ToString("O");
        }

        await store.CommitAsync();
        NotifyStatus("就绪");
    }

    private async Task IncrementalSyncAsync(CancellationToken ct)
    {
        await using var store = await _storeFactory.CreateStoreAsync();
        var cursor = await store.GetCursorAsync();
        int sinceVersion = cursor?.LastMaxVersion ?? 0;
        int maxVersion = sinceVersion;

        string? nextCursor = null;
        do
        {
            var response = await _api.GetFileTreeAsync(sinceVersion, cursor: nextCursor, ct: ct);
            if (response == null || response.Data.Length == 0)
            {
                break;
            }

            await _remoteApplier.ApplyRemoteChangesAsync(store, response, ct);
            nextCursor = response.HasMore ? response.NextCursor : null;
            if (response.MaxVersion > maxVersion)
            {
                maxVersion = response.MaxVersion;
            }
        }
        while (nextCursor != null && !ct.IsCancellationRequested);

        if (cursor != null)
        {
            if (maxVersion > cursor.LastMaxVersion)
            {
                cursor.LastMaxVersion = maxVersion;
                cursor.LastSyncAt = DateTime.UtcNow.ToString("O");
            }
        }
        else
        {
            // 游标不存在则创建（FullSyncAsync 失败后的恢复路径）
            store.AddCursor(new SyncCursor { Id = 1, LastMaxVersion = maxVersion, LastSyncAt = DateTime.UtcNow.ToString("O") });
        }

        await store.CommitAsync();
    }

    // 远程变更应用（ApplyRemoteChangesAsync/MakeSnapshot）T-099 已下沉至 SyncRemoteApplier
}
