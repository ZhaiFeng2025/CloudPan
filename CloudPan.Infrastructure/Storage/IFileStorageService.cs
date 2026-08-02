namespace CloudPan.Infrastructure.Storage;

/// <summary>
/// 物理文件存储服务接口。
/// </summary>
public interface IFileStorageService
{
    string GetAbsolutePath(string relativePath);
    string? ValidatePath(string relativePath);
    Task<string> ComputeHashAsync(string absolutePath, CancellationToken ct = default);
    Task<string?> AtomicWriteAsync(string relativePath, Stream content, string? expectedHash, CancellationToken ct = default);
    FileStream OpenRead(string relativePath);
    bool Exists(string relativePath);
    long GetSize(string relativePath);
    void Delete(string relativePath);
    void DeleteDirectory(string relativePath);
    void Move(string oldRelativePath, string newRelativePath);
    void CreateDirectory(string relativePath);
    Task<string> StoreVersionAsync(string relativePath, int version, CancellationToken ct = default);
    void EnsureSyncRootExists();
}
