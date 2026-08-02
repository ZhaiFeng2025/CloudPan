using System.Text.Json;
using CloudPan.Contract;
using CloudPan.Infrastructure.Storage;

namespace CloudPan.Server.Core;

/// <inheritdoc />
public class TrashService : ITrashService
{
    private readonly IFileStorageService _storage;
    private readonly IFileIndexService _index;
    private readonly IVersionService _version;

    public TrashService(IFileStorageService storage, IFileIndexService index, IVersionService version)
    {
        _storage = storage;
        _index = index;
        _version = version;
    }

    /// <inheritdoc />
    public Task<List<TrashItem>> ListAsync()
    {
        string trashDir = GetTrashDir();
        if (!Directory.Exists(trashDir))
        {
            return Task.FromResult(new List<TrashItem>());
        }

        var items = Directory.GetFiles(trashDir, "*.json")
            .Select(f =>
            {
                try
                {
                    string json = File.ReadAllText(f);
                    return JsonSerializer.Deserialize<TrashEntry>(json);
                }
                catch { return null; }
            })
            .Where(t => t != null)
            .OrderByDescending(t => t!.DeletedAt)
            .Select(t => new TrashItem(
                t!.OriginalPath,
                t.TrashFileName,
                t.FileSize,
                t.IsDirectory,
                t.DeletedAt,
                (DateTime.UtcNow - DateTime.Parse(t.DeletedAt)).Days))
            .ToList();

        return Task.FromResult(items);
    }

    /// <inheritdoc />
    public async Task<TrashRestoreResult> RestoreAsync(string metaFileName)
    {
        string trashDir = GetTrashDir();

        // R-A5 路径安全统一防线：MetaFileName 为用户输入，先经 Path.GetFileName 归一，
        // 再校验——禁止目录分隔符（'/' 或 '\\'，防 ../、..\\ 等路径穿越读取 trashDir 外文件），
        // 且归一后结果必须与输入一致（存在分隔符即会被剥离导致不一致，直接拒绝）
        string safeMetaFileName = Path.GetFileName(metaFileName);
        if (string.IsNullOrEmpty(safeMetaFileName)
            || safeMetaFileName != metaFileName
            || safeMetaFileName is "." or ".."
            || safeMetaFileName.IndexOfAny(new[] { '/', '\\' }) >= 0)
        {
            return new TrashRestoreResult(false, null,
                new DomainError(HttpErrorCode.BAD_REQUEST, "回收站记录文件名不合法", "回收站记录文件名不合法"));
        }

        string metaPath = Path.Combine(trashDir, safeMetaFileName);
        if (!File.Exists(metaPath))
        {
            return new TrashRestoreResult(false, null,
                new DomainError(HttpErrorCode.NOT_FOUND, "回收站记录不存在", "回收站记录不存在或已被清理"));
        }

        string json = File.ReadAllText(metaPath);
        var entry = JsonSerializer.Deserialize<TrashEntry>(json);
        if (entry == null)
        {
            return new TrashRestoreResult(false, null,
                new DomainError(HttpErrorCode.BAD_REQUEST, "回收站记录损坏", "回收站记录损坏，无法恢复该文件"));
        }

        // R-A5 纵深防御：回收站实体文件名经 GetFileName 归一、恢复目标路径经 ValidatePath 校验，
        // 防元数据被篡改写入越界路径（TrashFileName 指向 trashDir 外、OriginalPath 逃出同步根）
        string safeTrashFileName = Path.GetFileName(entry.TrashFileName);
        if (string.IsNullOrEmpty(safeTrashFileName) || safeTrashFileName != entry.TrashFileName)
        {
            return new TrashRestoreResult(false, null,
                new DomainError(HttpErrorCode.BAD_REQUEST, "回收站记录损坏", "回收站记录损坏，无法恢复该文件"));
        }

        string? targetPathErr = _storage.ValidatePath(entry.OriginalPath);
        if (targetPathErr != null)
        {
            return new TrashRestoreResult(false, null,
                new DomainError(HttpErrorCode.BAD_REQUEST, "回收站记录损坏", "回收站记录损坏，无法恢复该文件"));
        }

        string trashFile = Path.Combine(trashDir, safeTrashFileName);
        if (!File.Exists(trashFile) && !Directory.Exists(trashFile))
        {
            return new TrashRestoreResult(false, null,
                new DomainError(HttpErrorCode.NOT_FOUND, "文件已丢失", "回收站中的文件已丢失，无法恢复"));
        }

        // 恢复：移动回原位
        string targetPath = _storage.GetAbsolutePath(entry.OriginalPath);
        string? dir = Path.GetDirectoryName(targetPath);
        if (dir != null)
        {
            Directory.CreateDirectory(dir);
        }

        try
        {
            if (entry.IsDirectory)
            {
                Directory.Move(trashFile, targetPath);
                // 删除时子文件为墓碑（FileState.Deleting），恢复时递归 Upsert 将墓碑还原为 Synced，
                // 客户端增量同步据此恢复本地内容
                await ReindexDirectoryAsync(entry.OriginalPath);
            }
            else
            {
                File.Move(trashFile, targetPath, overwrite: false);
            }

            // 重建索引
            string? hash = entry.IsDirectory ? null : await _storage.ComputeHashAsync(targetPath);
            int newVersion = await _version.NextVersionAsync();
            var type = entry.IsDirectory ? FileType.Directory : FileType.File;
            await _index.UpsertFileAsync(entry.OriginalPath, type, hash,
                entry.IsDirectory ? 0 : new FileInfo(targetPath).Length,
                DateTime.UtcNow.ToString("O"), newVersion);

            // 删除元数据文件
            File.Delete(metaPath);

            return new TrashRestoreResult(true, entry.OriginalPath);
        }
        catch (Exception ex)
        {
            return new TrashRestoreResult(false, null,
                new DomainError(HttpErrorCode.INTERNAL_ERROR, $"恢复失败: {ex.Message}", "恢复文件时出现错误，请稍后重试"));
        }
    }

    /// <inheritdoc />
    public Task EmptyAsync()
    {
        string trashDir = GetTrashDir();
        if (Directory.Exists(trashDir))
        {
            try { Directory.Delete(trashDir, recursive: true); } catch { /* 尽力清理 */ }
            Directory.CreateDirectory(trashDir);
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task MoveToTrashAsync(string relativePath, bool isDirectory)
    {
        string trashDir = GetTrashDir();
        Directory.CreateDirectory(trashDir);

        string sourcePath = _storage.GetAbsolutePath(relativePath);
        string timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
        string fileName = Path.GetFileName(relativePath.TrimEnd('/'));
        string trashFileName = $"{timestamp}_{fileName}";
        string trashFilePath = Path.Combine(trashDir, trashFileName);

        if (isDirectory && Directory.Exists(sourcePath))
        {
            Directory.Move(sourcePath, trashFilePath);
        }
        else if (File.Exists(sourcePath))
        {
            File.Move(sourcePath, trashFilePath);
        }

        TrashEntry entry = new TrashEntry
        {
            OriginalPath = relativePath,
            TrashFileName = trashFileName,
            FileSize = isDirectory ? 0 : (File.Exists(trashFilePath) ? new FileInfo(trashFilePath).Length : 0),
            IsDirectory = isDirectory,
            DeletedAt = DateTime.UtcNow.ToString("O")
        };

        string metaPath = Path.Combine(trashDir, $"{timestamp}_{fileName}.json");
        File.WriteAllText(metaPath, JsonSerializer.Serialize(entry));

        return Task.CompletedTask;
    }

    /// <summary>递归重建目录下所有子文件/子目录的索引（回收站恢复目录时，删除操作已移除全部子 FileEntry）。</summary>
    private async Task ReindexDirectoryAsync(string dirPath)
    {
        string absDir = _storage.GetAbsolutePath(dirPath);
        string syncRoot = _storage.GetAbsolutePath("/");
        string ts = DateTime.UtcNow.ToString("O");

        // 递归枚举目录树（排除 .cloudpan 元数据目录）
        foreach (string fullPath in Directory.EnumerateFileSystemEntries(absDir, "*", SearchOption.AllDirectories))
        {
            if (fullPath.Contains($"{Path.DirectorySeparatorChar}.cloudpan{Path.DirectorySeparatorChar}"))
            {
                continue;
            }

            string relPath = "/" + Path.GetRelativePath(syncRoot, fullPath).Replace('\\', '/');
            int ver = await _version.NextVersionAsync();

            if (Directory.Exists(fullPath))
            {
                await _index.UpsertFileAsync(relPath, FileType.Directory, null, 0, ts, ver);
            }
            else
            {
                string hash = await _storage.ComputeHashAsync(fullPath);
                long size = new FileInfo(fullPath).Length;
                await _index.UpsertFileAsync(relPath, FileType.File, hash, size, ts, ver);
            }
        }
    }

    /// <summary>回收站元数据目录（位于同步根父目录的 .cloudpan 下）。</summary>
    private string GetTrashDir()
    {
        return Path.Combine(
            Path.GetDirectoryName(_storage.GetAbsolutePath("/"))!,
            ".cloudpan", ".trash");
    }

    /// <summary>回收站元数据记录。</summary>
    private class TrashEntry
    {
        public string OriginalPath { get; set; } = "";
        public string TrashFileName { get; set; } = "";
        public long FileSize { get; set; }
        public bool IsDirectory { get; set; }
        public string DeletedAt { get; set; } = "";
    }
}
