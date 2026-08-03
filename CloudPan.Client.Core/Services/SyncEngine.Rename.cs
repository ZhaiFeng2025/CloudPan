using CloudPan.Client.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CloudPan.Client.Core.Services;

/// <summary>SyncEngine 部分实现：目录重命名快照前缀跟随（T-066）。</summary>
public partial class SyncEngine
{
    /// <summary>
    /// T-066：目录重命名快照前缀跟随——将旧前缀下的全部子项快照（含目录自身）按新前缀重建，
    /// 内容/版本/落盘标记跟随，并清空旧前缀下的未决队列项。
    /// 否则子项快照停留在旧路径：FullScan 会把旧路径判为本地删除（整棵子树删除传播 + 服务端 404 噪音），
    /// 增量同步会把新路径判为远端新文件（整棵子树重下载）。
    /// </summary>
    private async Task RewriteSubtreeSnapshotsAsync(ClientDbContext db, SyncQueue item)
    {
        string oldKey = item.FilePath.TrimEnd('/');
        string oldDir = oldKey + "/";
        string newPrefix = item.TargetPath!.TrimEnd('/');

        // 1) 清空旧前缀下的未决队列项（重命名处理后旧路径不再有效，残留的 Delete/Upload/Download
        //    会产生服务端 404 删除噪音或 Delete 先于 Move 到达的回收站误删竞态）。
        //    排除当前 rename 项自身（由 ProcessQueueAsync 负责移除）。
        var staleQueue = await db.SyncQueue
            .Where(q => q.Id != item.Id
                && (q.FilePath == oldKey || q.FilePath.StartsWith(oldDir)))
            .ToListAsync();
        if (staleQueue.Count > 0)
        {
            db.SyncQueue.RemoveRange(staleQueue);
            _logger.LogInformation("目录重命名：清空旧前缀下 {Count} 个未决队列项", staleQueue.Count);
        }

        // 2) 快照前缀跟随：旧前缀下全部快照（含目录自身）→ 新前缀，字段整体保留
        var affected = await db.RemoteSnapshots
            .Where(s => s.Path == oldKey || s.Path.StartsWith(oldDir))
            .ToListAsync();
        if (affected.Count == 0)
        {
            return; // 目录快照缺失（目录从未同步）→ 无可跟随的子项
        }

        foreach (var snap in affected)
        {
            string suffix = snap.Path == oldKey ? "" : snap.Path[oldDir.Length..];
            string newPath = string.IsNullOrEmpty(suffix) ? newPrefix : newPrefix + "/" + suffix;

            // 目标路径已存在快照（重命名覆盖）时移除旧快照——重命名源胜出，避免主键冲突
            var existing = await db.RemoteSnapshots.FindAsync(newPath);
            if (existing != null)
            {
                db.RemoteSnapshots.Remove(existing);
            }

            db.RemoteSnapshots.Add(new RemoteSnapshot
            {
                Path = newPath,
                Type = snap.Type,
                Hash = snap.Hash,
                Size = snap.Size,
                Version = snap.Version,
                State = snap.State,
                LastModified = snap.LastModified,
                IsDownloaded = snap.IsDownloaded
            });
            db.RemoteSnapshots.Remove(snap);
        }
        _logger.LogInformation("目录重命名：快照前缀跟随 {Count} 项", affected.Count);
    }
}
