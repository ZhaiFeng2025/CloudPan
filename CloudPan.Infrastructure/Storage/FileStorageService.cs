namespace CloudPan.Infrastructure.Storage;

/// <summary>
/// 物理文件存储服务。
/// 管理同步根下的文件读写、SHA-256 计算、原子写入（.tmp → rename）。
/// </summary>
public class FileStorageService : IFileStorageService
{
    private readonly string _syncRoot;
    private readonly string _versionsDir;

    public FileStorageService(string syncRoot)
    {
        // 规范化路径：消除短文件名、正斜杠、末尾分隔符等差异
        _syncRoot = Path.GetFullPath(syncRoot).TrimEnd(Path.DirectorySeparatorChar);
        _versionsDir = Path.Combine(_syncRoot, ".cloudpan", ".versions");
        Directory.CreateDirectory(_versionsDir);
    }

    /// <summary>
    /// 将相对路径（如 /docs/report.docx）转为同步根下的绝对路径。
    /// F-132 起内建强制校验（防线收敛 Storage 单点，CLAUDE.md 8.5）：越界/非法路径抛异常，
    /// 任何调用方漏调 ValidatePath 也不会越界写/读。根路径（/）返回同步根本身，视为合法。
    /// </summary>
    public string GetAbsolutePath(string relativePath)
    {
        string? error = ValidatePathCore(relativePath);
        if (error != null)
        {
            throw new ArgumentException($"拒绝越界相对路径（{error}）: {relativePath}", nameof(relativePath));
        }

        // 去掉开头的 /
        return Path.Combine(_syncRoot, relativePath.TrimStart('/'));
    }

    /// <summary>
    /// 验证路径在同步根内，防止目录遍历攻击。
    /// 返回 null 表示合法，否则返回错误信息。与 GetAbsolutePath 共用 <see cref="ValidatePathCore"/>，
    /// 校验逻辑单一实现（F-132 防线收敛）。
    /// </summary>
    public string? ValidatePath(string relativePath)
        => ValidatePathCore(relativePath);

    /// <summary>
    /// 路径校验核心（Storage 单点）：空/空字符/越界（经 Path.GetFullPath 消解 .. 后必须仍在同步根内）。
    /// 根路径本身（GetAbsolutePath("/") → 同步根）视为合法；不可解析路径一律拒绝。
    /// </summary>
    private string? ValidatePathCore(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return "路径不能为空";
        }

        if (relativePath.Contains('\0'))
        {
            return "路径包含空字符";
        }

        try
        {
            // 规范化路径并检查是否越界
            string absolutePath = Path.GetFullPath(Path.Combine(_syncRoot, relativePath.TrimStart('/')));
            string rootPrefix = _syncRoot;
            if (!rootPrefix.EndsWith(Path.DirectorySeparatorChar))
            {
                rootPrefix += Path.DirectorySeparatorChar;
            }

            // 根路径本身（= 同步根，前缀含分隔符无法匹配）单独放行
            bool isRoot = string.Equals(absolutePath, _syncRoot, StringComparison.OrdinalIgnoreCase);
            if (!isRoot && !absolutePath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return "路径越界";
            }

            return null; // 合法
        }
        catch (Exception ex)
        {
            // 任意不可解析路径（非法字符等）一律拒绝——防御不可信输入，不抛给调用方（对齐客户端 LocalPathValidator）
            return $"路径无效: {ex.Message}";
        }
    }

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
                string actualHash = await FileHasher.ComputeSha256Async(tmpPath, ct);
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
    /// 删除 .versions 存档物理文件（孤儿存档清理单点）。幂等：storagePath 为空或文件不存在则无操作；
    /// IO 异常向上抛，由调用方记录（FileStorageService 不依赖日志设施）。
    /// </summary>
    public void DeleteVersionArchive(string? archiveStoragePath)
    {
        if (string.IsNullOrEmpty(archiveStoragePath))
        {
            return;
        }

        // F-132 防线收敛：Path.Combine 直拼前先校验落点仍在 .versions 内，拒绝含分隔符/.. 的路径
        // （storagePath 来自 DB 的 StoragePath，防御污染记录逃逸 .versions 目录，CLAUDE.md 8.5）
        string archiveFile = Path.Combine(_versionsDir, archiveStoragePath);
        string versionsPrefix = _versionsDir.EndsWith(Path.DirectorySeparatorChar)
            ? _versionsDir
            : _versionsDir + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(archiveFile).StartsWith(versionsPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (File.Exists(archiveFile))
        {
            File.Delete(archiveFile);
        }
    }

    /// <inheritdoc />
    public string GetThumbnailCachePath(string relativePath, string cacheName)
    {
        // 经 GetAbsolutePath 内建校验派生：源路径越界/非法即抛（T-090 防线），派生目录必在同步根内
        string srcDir = Path.GetDirectoryName(GetAbsolutePath(relativePath))
            ?? throw new ArgumentException($"无法解析源文件目录: {relativePath}", nameof(relativePath));

        // cacheName 仅允许纯文件名（如 {hash}.jpg）：拒绝分隔符/.. /空字符，防缓存键目录注入逃逸元数据目录
        if (string.IsNullOrWhiteSpace(cacheName)
            || cacheName.Contains(Path.DirectorySeparatorChar)
            || cacheName.Contains(Path.AltDirectorySeparatorChar)
            || cacheName == ".."
            || cacheName.Contains('\0'))
        {
            throw new ArgumentException($"非法缩略图缓存文件名: {cacheName}", nameof(cacheName));
        }

        // 布局单点：<源文件目录>/.cloudpan/.thumbnails/（与 T-088 EnumerateThumbnailCacheDirs 就近遍历同源）
        string thumbDir = Path.Combine(srcDir, ".cloudpan", ".thumbnails");
        Directory.CreateDirectory(thumbDir);
        return Path.Combine(thumbDir, cacheName);
    }

    /// <inheritdoc />
    public string GetChunkTempPath(string relativePath)
    {
        // 经 GetAbsolutePath 内建校验派生：源路径越界/非法即抛（T-090 防线），派生目录必在同步根内
        string srcDir = Path.GetDirectoryName(GetAbsolutePath(relativePath))
            ?? throw new ArgumentException($"无法解析源文件目录: {relativePath}", nameof(relativePath));

        // 布局单点：<源文件目录>/.cloudpan/<guid>.chunk.tmp
        string chunkDir = Path.Combine(srcDir, ".cloudpan");
        Directory.CreateDirectory(chunkDir);
        return Path.Combine(chunkDir, $"{Guid.NewGuid():N}.chunk.tmp");
    }

    /// <inheritdoc />
    public string GetThumbnailCacheDirUnder(string directoryPath)
        // 布局单点：<dir>/.cloudpan/.thumbnails（与写入 GetThumbnailCachePath 同源；调用方保证 dir 在同步根内）
        => Path.Combine(directoryPath, ".cloudpan", ".thumbnails");

    /// <summary>
    /// 检查同步根目录是否存在。
    /// </summary>
    public void EnsureSyncRootExists()
    {
        Directory.CreateDirectory(_syncRoot);
        Directory.CreateDirectory(Path.Combine(_syncRoot, ".cloudpan"));
    }
}
