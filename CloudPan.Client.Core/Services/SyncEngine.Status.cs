using CloudPan.Contract;
using Microsoft.EntityFrameworkCore;

namespace CloudPan.Client.Core.Services;

/// <summary>每文件同步状态视图项——供 UI 渲染每文件状态图标（✓↻!✗☁）。</summary>
public sealed record FileSyncStatusItem(string RelativePath, bool IsDirectory, int State, bool LocalExists);

/// <summary>SyncEngine 部分实现：每文件同步状态查询（T-009，供 UI 渲染状态图标）。</summary>
public partial class SyncEngine
{
    /// <summary>
    /// 读取同步目录每文件的当前同步状态：
    /// 服务端快照 FileState（Synced/CloudOnly/Deleting/Modified）+ 待处理队列（Uploading/Downloading/Deleting）+ 本地存在性。
    /// 冲突与错误由 UI 依据本地维护的冲突/错误列表叠加，不在此查询（避免与 UI 状态源重复）。
    /// </summary>
    public async Task<IReadOnlyList<FileSyncStatusItem>> GetFileSyncStatusesAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        // 1. 待处理队列 → 瞬态状态（Uploading/Downloading/Deleting），优先级高于快照状态
        var queueOps = await db.SyncQueue
            .Where(q => q.Operation == (int)SyncOperation.Upload
                     || q.Operation == (int)SyncOperation.Download
                     || q.Operation == (int)SyncOperation.Delete)
            .ToListAsync(ct);
        var queueStateByPath = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var q in queueOps)
        {
            int state = q.Operation switch
            {
                (int)SyncOperation.Upload => (int)FileState.Uploading,
                (int)SyncOperation.Download => (int)FileState.Downloading,
                _ => (int)FileState.Deleting
            };
            queueStateByPath[q.FilePath] = state;
        }

        // 2. 服务端快照（Synced/CloudOnly/Deleting/Modified）
        var snapshots = await db.RemoteSnapshots.ToListAsync(ct);

        // 3. 本地文件/目录集合（相对路径，忽略 .cloudpan 与忽略规则）
        HashSet<string> localFiles = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> localDirs = new(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(NormalizePath(_syncRoot)))
        {
            foreach (string fullPath in Directory.EnumerateFileSystemEntries(NormalizePath(_syncRoot), "*", SearchOption.AllDirectories))
            {
                if (ShouldIgnoreScan(fullPath))
                {
                    continue;
                }

                string rel = ToRelativePath(fullPath);
                if (Directory.Exists(fullPath))
                {
                    localDirs.Add(rel);
                }
                else
                {
                    localFiles.Add(rel);
                }
            }
        }

        var results = new List<FileSyncStatusItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 4a. 快照项（含 CloudOnly 远端文件——本地无副本但用户需看到状态）
        foreach (var snap in snapshots)
        {
            seen.Add(snap.Path);
            bool isDir = snap.Type == (int)FileType.Directory;
            bool localExists = isDir ? localDirs.Contains(snap.Path) : localFiles.Contains(snap.Path);
            int state = queueStateByPath.TryGetValue(snap.Path, out int qState) ? qState : snap.State;
            results.Add(new FileSyncStatusItem(snap.Path, isDir, state, localExists));
        }

        // 4b. 本地有、快照无的文件/目录 → 新文件待上传（Modified/Uploading）
        foreach (string rel in localFiles)
        {
            if (seen.Contains(rel))
            {
                continue;
            }

            seen.Add(rel);
            int state = queueStateByPath.TryGetValue(rel, out int qState) ? qState : (int)FileState.Modified;
            results.Add(new FileSyncStatusItem(rel, false, state, true));
        }

        foreach (string rel in localDirs)
        {
            if (seen.Contains(rel))
            {
                continue;
            }

            seen.Add(rel);
            int state = queueStateByPath.TryGetValue(rel, out int qState) ? qState : (int)FileState.Modified;
            results.Add(new FileSyncStatusItem(rel, true, state, true));
        }

        // 按路径排序，便于逐文件定位
        results.Sort((a, b) => string.Compare(a.RelativePath, b.RelativePath, StringComparison.OrdinalIgnoreCase));
        return results;
    }
}
