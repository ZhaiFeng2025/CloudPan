namespace CloudPan.Infrastructure.Storage;

/// <summary>
/// 物理文件存储服务接口。
/// </summary>
public interface IFileStorageService
{
    string GetAbsolutePath(string relativePath);
    string? ValidatePath(string relativePath);
    Task<string?> AtomicWriteAsync(string relativePath, Stream content, string? expectedHash, CancellationToken ct = default);
    FileStream OpenRead(string relativePath);
    bool Exists(string relativePath);
    long GetSize(string relativePath);
    void Delete(string relativePath);
    void DeleteDirectory(string relativePath);
    void Move(string oldRelativePath, string newRelativePath);
    void CreateDirectory(string relativePath);
    Task<string> StoreVersionAsync(string relativePath, int version, CancellationToken ct = default);

    /// <summary>
    /// 删除 .versions 存档物理文件（孤儿存档清理单点，VersionCommitHelper/FileIndexService 共用）。
    /// 路径构造收敛于此（本服务持有 .versions 目录），避免调用方各自拼接。storagePath 为空或文件不存在则无操作；
    /// IO 异常向上抛，由调用方记录。幂等。
    /// </summary>
    void DeleteVersionArchive(string? archiveStoragePath);

    /// <summary>
    /// 返回源文件对应的缩略图缓存文件绝对路径（目录不存在则创建）。
    /// 元数据布局单点：<源文件目录>/.cloudpan/.thumbnails/&lt;cacheName&gt;——与回收遍历
    /// （T-088 EnumerateThumbnailCacheDirs 就近遍历）同源，改布局只改本服务。源路径经 GetAbsolutePath
    /// 校验派生（T-090 内建防线，越界/非法即抛，不绕过）；cacheName 仅允许纯文件名（拒绝分隔符/..，
    /// 防缓存键目录注入逃逸元数据目录，CLAUDE.md 8.5）。
    /// </summary>
    string GetThumbnailCachePath(string relativePath, string cacheName);

    /// <summary>
    /// 返回分块上传临时文件绝对路径（唯一命名，目录不存在则创建）。
    /// 元数据布局单点：<源文件目录>/.cloudpan/&lt;guid&gt;.chunk.tmp，改布局只改本服务。
    /// 源路径经 GetAbsolutePath 校验派生（T-090 内建防线，越界/非法即抛，不绕过）。
    /// </summary>
    string GetChunkTempPath(string relativePath);

    /// <summary>
    /// 返回目录下缩略图缓存目录的布局路径（&lt;dir&gt;/.cloudpan/.thumbnails），仅做路径组合、无 FS 操作。
    /// 布局单点：回收遍历（T-088 EnumerateThumbnailCacheDirs）与写入（GetThumbnailCachePath）同源，
    /// 改布局只改本服务。调用方须保证 directoryPath 为同步根内的绝对目录（遍历场景由同步根枚举保证，不越界）。
    /// </summary>
    string GetThumbnailCacheDirUnder(string directoryPath);

    void EnsureSyncRootExists();
}
