using CloudPan.Server;
using CloudPan.Server.Services;
using CloudPan.Shared;
using Microsoft.AspNetCore.Mvc;

namespace CloudPan.Server.Controllers;

/// <summary>
/// 回收站 API——浏览、恢复、清空已删除文件（保留 30 天）。
/// </summary>
[ApiController]
[Route("api/trash")]
[EndpointAuth(AuthMode.Token)]
public class TrashController : ControllerBase
{
    private readonly IFileStorageService _storage;
    private readonly IFileIndexService _index;
    private readonly IVersionService _version;

    public TrashController(IFileStorageService storage, IFileIndexService index, IVersionService version)
    {
        _storage = storage;
        _index = index;
        _version = version;
    }

    /// <summary>GET /api/trash — 列出回收站内容。</summary>
    [HttpGet]
    public IActionResult ListTrash()
    {
        string trashDir = Path.Combine(Path.GetDirectoryName(_storage.GetAbsolutePath("/"))!, ".cloudpan", ".trash");
        if (!Directory.Exists(trashDir))
        {
            return Ok(new { data = Array.Empty<object>() });
        }

        var items = Directory.GetFiles(trashDir, "*.json")
            .Select(f =>
            {
                try
                {
                    string json = System.IO.File.ReadAllText(f);
                    return System.Text.Json.JsonSerializer.Deserialize<TrashEntry>(json);
                }
                catch { return null; }
            })
            .Where(t => t != null)
            .OrderByDescending(t => t!.DeletedAt)
            .Select(t => new
            {
                t!.OriginalPath,
                t.TrashFileName,
                t.FileSize,
                t.IsDirectory,
                t.DeletedAt,
                AgeDays = (DateTime.UtcNow - DateTime.Parse(t.DeletedAt)).Days
            })
            .ToList();

        return Ok(new { data = items });
    }

    /// <summary>POST /api/trash/restore — 恢复文件。</summary>
    [HttpPost("restore")]
    public async Task<IActionResult> Restore([FromBody] RestoreTrashRequest request)
    {
        string trashDir = GetTrashDir();
        string metaPath = Path.Combine(trashDir, request.MetaFileName);
        if (!System.IO.File.Exists(metaPath))
        {
            return this.Error(HttpErrorCode.NOT_FOUND, "回收站记录不存在", "回收站记录不存在或已被清理");
        }

        string json = System.IO.File.ReadAllText(metaPath);
        var entry = System.Text.Json.JsonSerializer.Deserialize<TrashEntry>(json);
        if (entry == null)
        {
            return this.Error(HttpErrorCode.BAD_REQUEST, "回收站记录损坏", "回收站记录损坏，无法恢复该文件");
        }

        string trashFile = Path.Combine(trashDir, entry.TrashFileName);
        if (!System.IO.File.Exists(trashFile) && !Directory.Exists(trashFile))
        {
            return this.Error(HttpErrorCode.NOT_FOUND, "文件已丢失", "回收站中的文件已丢失，无法恢复");
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
                // 删除时子文件的 FileEntry 已被移除，恢复时递归重建子文件索引，避免客户端看不到恢复的内容
                await ReindexDirectoryAsync(entry.OriginalPath);
            }
            else
            {
                System.IO.File.Move(trashFile, targetPath, overwrite: false);
            }

            // 重建索引
            string? hash = entry.IsDirectory ? null : await _storage.ComputeHashAsync(targetPath);
            int newVersion = await _version.NextVersionAsync();
            var type = entry.IsDirectory ? CloudPan.Shared.FileType.Directory : CloudPan.Shared.FileType.File;
            await _index.UpsertFileAsync(entry.OriginalPath, type, hash,
                entry.IsDirectory ? 0 : new System.IO.FileInfo(targetPath).Length,
                DateTime.UtcNow.ToString("O"), newVersion);

            // 删除元数据文件
            System.IO.File.Delete(metaPath);

            return Ok(new { data = new { restored = entry.OriginalPath } });
        }
        catch (Exception ex)
        {
            return this.Error(HttpErrorCode.INTERNAL_ERROR, $"恢复失败: {ex.Message}", "恢复文件时出现错误，请稍后重试");
        }
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
                await _index.UpsertFileAsync(relPath, CloudPan.Shared.FileType.Directory, null, 0, ts, ver);
            }
            else
            {
                string hash = await _storage.ComputeHashAsync(fullPath);
                long size = new FileInfo(fullPath).Length;
                await _index.UpsertFileAsync(relPath, CloudPan.Shared.FileType.File, hash, size, ts, ver);
            }
        }
    }

    /// <summary>DELETE /api/trash/empty — 清空回收站。</summary>
    [HttpDelete("empty")]
    public IActionResult EmptyTrash()
    {
        string trashDir = GetTrashDir();
        if (Directory.Exists(trashDir))
        {
            try { Directory.Delete(trashDir, recursive: true); } catch { }
            Directory.CreateDirectory(trashDir);
        }
        return Ok(new { data = "trash emptied" });
    }

    // ============================================================
    // 工具方法
    // ============================================================

    internal static string GetTrashDir(IFileStorageService storage)
    {
        return Path.Combine(
            Path.GetDirectoryName(storage.GetAbsolutePath("/"))!,
            ".cloudpan", ".trash");
    }

    private string GetTrashDir() => GetTrashDir(_storage);

    /// <summary>将文件移入回收站。</summary>
    internal static void MoveToTrash(IFileStorageService storage, string relativePath, bool isDirectory)
    {
        string trashDir = GetTrashDir(storage);
        Directory.CreateDirectory(trashDir);

        string sourcePath = storage.GetAbsolutePath(relativePath);
        string timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
        string fileName = Path.GetFileName(relativePath.TrimEnd('/'));
        string trashFileName = $"{timestamp}_{fileName}";
        string trashFilePath = Path.Combine(trashDir, trashFileName);

        if (isDirectory && Directory.Exists(sourcePath))
        {
            Directory.Move(sourcePath, trashFilePath);
        }
        else if (System.IO.File.Exists(sourcePath))
        {
            System.IO.File.Move(sourcePath, trashFilePath);
        }

        TrashEntry entry = new TrashEntry
        {
            OriginalPath = relativePath,
            TrashFileName = trashFileName,
            FileSize = isDirectory ? 0 : (System.IO.File.Exists(trashFilePath) ? new System.IO.FileInfo(trashFilePath).Length : 0),
            IsDirectory = isDirectory,
            DeletedAt = DateTime.UtcNow.ToString("O")
        };

        string metaPath = Path.Combine(trashDir, $"{timestamp}_{fileName}.json");
        System.IO.File.WriteAllText(metaPath,
            System.Text.Json.JsonSerializer.Serialize(entry));
    }

    private class TrashEntry
    {
        public string OriginalPath { get; set; } = "";
        public string TrashFileName { get; set; } = "";
        public long FileSize { get; set; }
        public bool IsDirectory { get; set; }
        public string DeletedAt { get; set; } = "";
    }
}

/// <summary>从回收站恢复文件的请求，MetaFileName 为回收站元数据文件名。</summary>
public record RestoreTrashRequest(string MetaFileName);
