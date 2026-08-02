namespace CloudPan.Server.Core;

/// <summary>回收站条目（列表展示用）。</summary>
public sealed record TrashItem(string OriginalPath, string TrashFileName, long FileSize, bool IsDirectory, string DeletedAt, int AgeDays);

/// <summary>回收站恢复结果。</summary>
public sealed record TrashRestoreResult(bool Success, string? OriginalPath, DomainError? Error = null);

/// <summary>
/// 回收站领域服务。封装回收站列表/恢复/清空/移入，
/// 以及恢复目录时的递归重建索引（DB+FS 一致性由本服务保证，F-02 下沉载体）。
/// </summary>
public interface ITrashService
{
    /// <summary>列出回收站内容（按删除时间倒序）。</summary>
    Task<List<TrashItem>> ListAsync();

    /// <summary>恢复文件/目录到原位并重建索引。</summary>
    Task<TrashRestoreResult> RestoreAsync(string metaFileName);

    /// <summary>清空回收站。</summary>
    Task EmptyAsync();

    /// <summary>将文件/目录移入回收站（写入元数据记录）。</summary>
    Task MoveToTrashAsync(string relativePath, bool isDirectory);
}
