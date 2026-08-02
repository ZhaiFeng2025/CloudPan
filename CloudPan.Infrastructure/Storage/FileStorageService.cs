using CloudPan.Contract;

namespace CloudPan.Infrastructure.Storage;

/// <summary>
/// 物理文件存储服务。
/// 管理同步根下的文件读写、SHA-256 计算、原子写入（.tmp → rename）。
/// </summary>
public class FileStorageService : IFileStorageService
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
        string cleanPath = relativePath.TrimStart('/');
        return Path.Combine(_syncRoot, cleanPath);
    }

    /// <summary>
    /// 验证路径在同步根内，防止目录遍历攻击。
    /// 返回 null 表示合法，否则返回错误信息。
    /// </summary>
    public string? ValidatePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return "路径不能为空";
        }

        if (relativePath.Contains('\0'))
        {
            return "路径包含空字符";
        }

        // 规范化路径并检查是否越界
        string absolutePath = Path.GetFullPath(GetAbsolutePath(relativePath));
        string rootPrefix = _syncRoot;
        if (!rootPrefix.EndsWith(Path.DirectorySeparatorChar))
        {
            rootPrefix += Path.DirectorySeparatorChar;
        }

        System.Diagnostics.Debug.WriteLine($"[ValidatePath] absolute={absolutePath}, root={rootPrefix}");

        if (!absolutePath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return "路径越界";
        }

        return null; // 合法
    }

    /// <summary>
    /// 计算文件的 SHA-256 哈希（64 字符十六进制）。
    /// 单一实现委托给 CloudPan.Contract.FileHasher（T-017 收敛，哈希策略只改一处）。
    /// </summary>
    public async Task<string> ComputeHashAsync(string absolutePath, CancellationToken ct = default)
        => await FileHasher.ComputeSha256Async(absolutePath, ct);

    /// <summary>
    /// 原子写入：先写 .tmp → 校验哈希 → rename 到目标路径。
    /// 返回 null 表示成功，否则返回错误信息。
    /// </summary>
    public async Task<string?> AtomicWriteAsync(
        string relativePath, Stream content, string? expectedHash, CancellationToken ct = default)
    {
        string targetPath = GetAbsolutePath(relativePath);
        string? dir = Path.GetDirectoryName(targetPath);
        if (dir != null)
        {
            Directory.CreateDirectory(dir);
        }

        string tmpPath = targetPath + ".tmp";

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
                string actualHash = await ComputeHashAsync(tmpPath, ct);
                if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(tmpPath);
                    return $"哈希校验失败。期望: {expectedHash}, 实际: {actualHash}";
                }
            }

            // 原子 rename：目标文件可能被瞬时锁定（杀毒软件/共享目录观察者等），带退避重试
            string? moveError = await MoveWithRetryAsync(tmpPath, targetPath, ct);
            if (moveError != null)
            {
                // 持久锁：清理临时文件并返回友好错误，避免未处理异常 → 500（与哈希校验失败路径一致）
                if (File.Exists(tmpPath))
                {
                    try { File.Delete(tmpPath); } catch { /* 尽力而为 */ }
                }
                return moveError;
            }
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
    /// 带退避重试的原子重命名。Windows 上目标文件可能被瞬时锁定
    /// （杀毒扫描、共享目录观察者、另一进程刚写入等），瞬态锁重试 3 次（200/400ms 退避）；
    /// 持久锁返回友好错误字符串，而非抛出异常导致 500。
    /// </summary>
    private static async Task<string?> MoveWithRetryAsync(string tmpPath, string targetPath, CancellationToken ct)
    {
        const int maxAttempts = 3;
        Exception? lastError = null;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                File.Move(tmpPath, targetPath, overwrite: true);
                return null; // 成功
            }
            catch (IOException ex) { lastError = ex; }
            catch (UnauthorizedAccessException ex) { lastError = ex; }

            if (attempt < maxAttempts)
            {
                await Task.Delay(200 * attempt, ct); // 200ms / 400ms 退避
            }
        }
        return $"目标文件被占用（{lastError?.GetType().Name}），请稍后重试";
    }

    /// <summary>
    /// 打开文件读取流。
    /// </summary>
    public FileStream OpenRead(string relativePath)
    {
        string path = GetAbsolutePath(relativePath);
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
        string path = GetAbsolutePath(relativePath);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// 递归删除文件夹。
    /// </summary>
    public void DeleteDirectory(string relativePath)
    {
        string path = GetAbsolutePath(relativePath);
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    /// <summary>
    /// 移动/重命名文件。
    /// </summary>
    public void Move(string oldRelativePath, string newRelativePath)
    {
        string src = GetAbsolutePath(oldRelativePath);
        string dst = GetAbsolutePath(newRelativePath);
        string? dstDir = Path.GetDirectoryName(dst);
        if (dstDir != null)
        {
            Directory.CreateDirectory(dstDir);
        }

        File.Move(src, dst);
    }

    /// <summary>
    /// 创建文件夹。
    /// </summary>
    public void CreateDirectory(string relativePath)
    {
        string path = GetAbsolutePath(relativePath);
        Directory.CreateDirectory(path);
    }

    /// <summary>
    /// 存储版本历史文件到 .versions/ 目录。
    /// </summary>
    public async Task<string> StoreVersionAsync(string relativePath, int version, CancellationToken ct = default)
    {
        string srcPath = GetAbsolutePath(relativePath);
        if (!File.Exists(srcPath))
        {
            throw new FileNotFoundException("源文件不存在", srcPath);
        }

        string fileName = Path.GetFileName(relativePath);
        string nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
        string ext = Path.GetExtension(fileName);
        string versionFileName = $"{nameWithoutExt}_v{version}_{DateTime.UtcNow:yyyyMMdd}{ext}";
        string versionPath = Path.Combine(_versionsDir, versionFileName);

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
