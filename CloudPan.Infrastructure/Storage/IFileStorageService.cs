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

    void EnsureSyncRootExists();
}
