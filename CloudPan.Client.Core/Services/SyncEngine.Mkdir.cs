using CloudPan.Contract;
using CloudPan.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CloudPan.Client.Core.Services;

/// <summary>
/// SyncEngine 部分实现：目录 mkdir 同步（T-046——目录成为同步一等公民）。
/// 目录经 Upload 队列承载（ProcessUploadAsync 对目录转发到此处），调服务端 MkdirAsync
/// 建立目录 FileEntry 行，并记录目录快照；空目录由此在其他设备可见。
/// </summary>
public partial class SyncEngine
{
    /// <summary>
    /// 目录 mkdir 同步：调服务端 MkdirAsync 建立目录条目并记录/更新目录快照（非文件上传）。
    /// 独立 partial 文件承载，避免 Transfers.cs 单文件超行数上限。
    /// </summary>
    private async Task<bool> ProcessMkdirAsync(SyncQueue item, CancellationToken ct)
    {
        await _api.MkdirAsync(item.FilePath, ct);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var snapshot = await db.RemoteSnapshots.FindAsync(item.FilePath);
        if (snapshot != null)
        {
            snapshot.Type = (int)FileType.Directory;
            snapshot.State = (int)FileState.Synced;
            snapshot.IsDownloaded = true; // 目录本地即落盘
        }
        else
        {
            db.RemoteSnapshots.Add(new RemoteSnapshot
            {
                Path = item.FilePath,
                Type = (int)FileType.Directory,
                Hash = null,
                Size = 0,
                Version = item.BaseVersion ?? 0,
                State = (int)FileState.Synced,
                IsDownloaded = true
            });
        }
        await db.SaveChangesAsync();

        _logger.LogInformation("目录 mkdir 同步完成: {Path}", item.FilePath);
        return true;
    }
}
