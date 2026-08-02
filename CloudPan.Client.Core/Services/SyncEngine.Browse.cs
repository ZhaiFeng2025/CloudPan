using CloudPan.Client.Models;
using CloudPan.Shared;
using Microsoft.EntityFrameworkCore;

namespace CloudPan.Client.Services;

/// <summary>文件浏览项——供文件浏览视图（列表/网格）渲染，数据源为 /api/tree 快照（RemoteSnapshots 本地缓存）+ 本地文件系统。</summary>
public sealed record FileBrowseItem(
    string Path,
    string Name,
    bool IsDirectory,
    long Size,
    int Version,
    int State,
    bool LocalExists);

/// <summary>SyncEngine 部分实现：文件浏览数据查询（T-013，主窗口文件浏览主视图的数据来源）。</summary>
public partial class SyncEngine
{
    /// <summary>
    /// 返回浏览视图数据：目录模式下返回 <paramref name="directoryPath"/> 的直接子项；
    /// 搜索模式下返回其下所有路径中名称包含关键字的项（含深层子目录，递归定位文件）。
    /// 快照（RemoteSnapshots，即 /api/tree 拉取结果缓存）覆盖服务端文件（含 CloudOnly），
    /// 本地有而快照无的项并入（Modified/Uploading 瞬态）。
    /// 墓碑（Deleting）项不展示（删除中的文件从浏览视图消失）。
    /// </summary>
    public async Task<IReadOnlyList<FileBrowseItem>> GetFileBrowserAsync(
        string directoryPath, string? searchText = null, CancellationToken ct = default)
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

        // 2. 服务端快照（来自 /api/tree，本地 DB 缓存）
        var snapshots = await db.RemoteSnapshots.ToListAsync(ct);
        var snapshotByPath = new Dictionary<string, RemoteSnapshot>(StringComparer.OrdinalIgnoreCase);
        foreach (var snap in snapshots)
        {
            snapshotByPath[snap.Path] = snap;
        }

        // 3. 本地文件系统（新增本地文件/目录 → Modified/Uploading）
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

        // 4. 归一化浏览路径："/" 根或 "/a/b" 形式（无尾斜杠）
        string normDir = NormalizePath(directoryPath) ?? "/";
        normDir = normDir.Replace('\\', '/').TrimEnd('/');
        if (normDir.Length == 0 || !normDir.StartsWith('/'))
        {
            normDir = "/" + normDir.TrimStart('/');
        }
        string dirPrefix = normDir == "/" ? "/" : normDir + "/";

        bool searching = !string.IsNullOrWhiteSpace(searchText);
        string needle = searching ? searchText!.Trim().ToLowerInvariant() : "";

        var items = new Dictionary<string, FileBrowseItem>(StringComparer.OrdinalIgnoreCase);

        void AddOrMerge(string rawPath, bool isDir, long size, int version, int state, bool localExists)
        {
            // 删除中的项（本地删除排队/服务端墓碑）从浏览视图消失
            if (state == (int)FileState.Deleting)
            {
                return;
            }

            // 路径归一化：去尾斜杠，避免目录路径 "/a/b/" 与 "/a/b" 重复
            string path = rawPath.TrimEnd('/');
            if (path.Length == 0)
            {
                return;
            }

            if (!path.StartsWith(dirPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            string rest = path[dirPrefix.Length..];
            if (rest.Length == 0)
            {
                return;
            }

            if (!searching)
            {
                // 目录模式：仅直接子项（rest 不含分隔符）
                if (rest.Contains('/'))
                {
                    return;
                }
            }
            else
            {
                string name = path[(path.LastIndexOf('/') + 1)..];
                if (!name.ToLowerInvariant().Contains(needle))
                {
                    return;
                }
            }

            if (items.ContainsKey(path))
            {
                return;
            }

            string displayName = path[(path.LastIndexOf('/') + 1)..];
            items[path] = new FileBrowseItem(path, displayName, isDir, size, version, state, localExists);
        }

        // 5. 快照项（含 CloudOnly；Deleting 墓碑由 AddOrMerge 跳过）
        foreach (var snap in snapshots)
        {
            bool isDir = snap.Type == (int)FileType.Directory;
            bool localExists = isDir ? localDirs.Contains(snap.Path) : localFiles.Contains(snap.Path);
            int state = queueStateByPath.TryGetValue(snap.Path, out int qState) ? qState : snap.State;
            AddOrMerge(snap.Path, isDir, snap.Size, snap.Version, state, localExists);
        }

        // 6. 本地新增项（快照无）——作为 Modified/Uploading 并入
        foreach (string rel in localFiles)
        {
            if (snapshotByPath.ContainsKey(rel))
            {
                continue;
            }

            int state = queueStateByPath.TryGetValue(rel, out int qState) ? qState : (int)FileState.Modified;
            long size = 0;
            try
            {
                size = new FileInfo(ToLocalPath(rel)).Length;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"读取本地文件大小失败 {rel}: {ex.Message}");
            }

            AddOrMerge(rel, false, size, 0, state, true);
        }

        foreach (string rel in localDirs)
        {
            if (snapshotByPath.ContainsKey(rel))
            {
                continue;
            }

            int state = queueStateByPath.TryGetValue(rel, out int qState) ? qState : (int)FileState.Modified;
            AddOrMerge(rel, true, 0, 0, state, true);
        }

        var result = items.Values.ToList();
        // 默认排序：目录优先，同类型按名称（UI 可按需重排）
        result.Sort((a, b) =>
        {
            int byDir = (b.IsDirectory ? 1 : 0).CompareTo(a.IsDirectory ? 1 : 0);
            if (byDir != 0)
            {
                return byDir;
            }

            return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });
        return result;
    }
}
