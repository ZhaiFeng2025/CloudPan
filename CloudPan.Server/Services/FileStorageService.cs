using System.Security.Cryptography;

namespace CloudPan.Server.Services;

/// <summary>
/// 物理文件存储服务。
/// 管理同步根下的文件读写、SHA-256 计算、原子写入（.tmp → rename）。
/// </summary>
public class FileStorageService
{
    private readonly string _syncRoot;
    private readonly string _versionsDir;
    private readonly string _thumbnailsDir;

    public FileStorageService(string syncRoot)
    {
        // 规范化路径：消除短文件名、正斜杠、末尾分隔符等差异
        _syncRoot = Path.GetFullPath(syncRoot).TrimEnd(Path.DirectorySeparatorChar);
        _versionsDir = Path.Combine(_syncRoot, ".cloudpan", ".versions");
        _thumbnailsDir = Path.Combine(_syncRoot, ".cloudpan", ".thumbnails");
        Directory.CreateDirectory(_versionsDir);
        Directory.CreateDirectory(_thumbnailsDir);
    }

    /// <summary>
    /// 将相对路径（如 /docs/report.docx）转为同步根下的绝对路径。
    /// </summary>
    public string GetAbsolutePath(string relativePath)
    {
        // 去掉开头的 /
        var cleanPath = relativePath.TrimStart('/');
        return Path.Combine(_syncRoot, cleanPath);
    }

    /// <summary>
    /// 验证路径在同步根内，防止目录遍历攻击。
    /// 返回 null 表示合法，否则返回错误信息。
    /// </summary>
    public string? ValidatePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            return "路径不能为空";
        if (relativePath.Contains(".."))
            return "路径包含非法字符 (..)";
        if (relativePath.Contains('\0'))
            return "路径包含空字符";

        // Path.GetFullPath 同时应用于 rootPath 和 absolutePath，
        // 保证短文件名/正斜杠/大小写规范化后前缀一致
        var absolutePath = Path.GetFullPath(GetAbsolutePath(relativePath));
        var rootPath = Path.GetFullPath(_syncRoot);
        if (!rootPath.EndsWith(Path.DirectorySeparatorChar))
            rootPath += Path.DirectorySeparatorChar;

        if (!absolutePath.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase))
            return "路径越界";

        return null; // 合法
    }

    /// <summary>
    /// 计算文件的 SHA-256 哈希（64 字符十六进制）。
    /// </summary>
    public async Task<string> ComputeHashAsync(string absolutePath, CancellationToken ct = default)
    {
        using var sha = SHA256.Create();
        await using var stream = File.OpenRead(absolutePath);
        var hash = await sha.ComputeHashAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// 原子写入：先写 .tmp → 校验哈希 → rename 到目标路径。
    /// 返回 null 表示成功，否则返回错误信息。
    /// </summary>
    public async Task<string?> AtomicWriteAsync(
        string relativePath, Stream content, string? expectedHash, CancellationToken ct = default)
    {
        var targetPath = GetAbsolutePath(relativePath);
        var dir = Path.GetDirectoryName(targetPath);
        if (dir != null) Directory.CreateDirectory(dir);

        var tmpPath = targetPath + ".tmp";

        try
        {
            // 写入临时文件
            await using (var tmpStream = File.Create(tmpPath))
            {
                await content.CopyToAsync(tmpStream, ct);
                await tmpStream.FlushAsync(ct);
            }

            // 校验哈希
            if (expectedHash != null)
            {
                var actualHash = await ComputeHashAsync(tmpPath, ct);
                if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(tmpPath);
                    return $"哈希校验失败。期望: {expectedHash}, 实际: {actualHash}";
                }
            }

            // 原子 rename
            File.Move(tmpPath, targetPath, overwrite: true);
            return null; // 成功
        }
        catch
        {
            // 清理残留的临时文件
            if (File.Exists(tmpPath))
            {
                try { File.Delete(tmpPath); } catch { /* 尽力而为 */ }
            }
            throw;
        }
    }

    /// <summary>
    /// 打开文件读取流。
    /// </summary>
    public FileStream OpenRead(string relativePath)
    {
        var path = GetAbsolutePath(relativePath);
        return File.OpenRead(path);
    }

    /// <summary>
    /// 检查文件是否存在。
    /// </summary>
    public bool Exists(string relativePath)
    {
        return File.Exists(GetAbsolutePath(relativePath));
    }

    /// <summary>
    /// 获取文件大小（字节）。
    /// </summary>
    public long GetSize(string relativePath)
    {
        return new FileInfo(GetAbsolutePath(relativePath)).Length;
    }

    /// <summary>
    /// 删除文件。
    /// </summary>
    public void Delete(string relativePath)
    {
        var path = GetAbsolutePath(relativePath);
        if (File.Exists(path))
            File.Delete(path);
    }

    /// <summary>
    /// 递归删除文件夹。
    /// </summary>
    public void DeleteDirectory(string relativePath)
    {
        var path = GetAbsolutePath(relativePath);
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
    }

    /// <summary>
    /// 移动/重命名文件。
    /// </summary>
    public void Move(string oldRelativePath, string newRelativePath)
    {
        var src = GetAbsolutePath(oldRelativePath);
        var dst = GetAbsolutePath(newRelativePath);
        var dstDir = Path.GetDirectoryName(dst);
        if (dstDir != null) Directory.CreateDirectory(dstDir);
        File.Move(src, dst);
    }

    /// <summary>
    /// 创建文件夹。
    /// </summary>
    public void CreateDirectory(string relativePath)
    {
        var path = GetAbsolutePath(relativePath);
        Directory.CreateDirectory(path);
    }

    /// <summary>
    /// 存储版本历史文件到 .versions/ 目录。
    /// </summary>
    public async Task<string> StoreVersionAsync(string relativePath, int version, CancellationToken ct = default)
    {
        var srcPath = GetAbsolutePath(relativePath);
        if (!File.Exists(srcPath)) throw new FileNotFoundException("源文件不存在", srcPath);

        var fileName = Path.GetFileName(relativePath);
        var nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        var versionFileName = $"{nameWithoutExt}_v{version}_{DateTime.UtcNow:yyyyMMdd}{ext}";
        var versionPath = Path.Combine(_versionsDir, versionFileName);

        Directory.CreateDirectory(_versionsDir);
        using var srcStream = File.OpenRead(srcPath);
        using var dstStream = File.Create(versionPath);
        await srcStream.CopyToAsync(dstStream, ct);

        return versionFileName;
    }

    /// <summary>
    /// 检查同步根目录是否存在。
    /// </summary>
    public void EnsureSyncRootExists()
    {
        Directory.CreateDirectory(_syncRoot);
        Directory.CreateDirectory(Path.Combine(_syncRoot, ".cloudpan"));
    }
}
