using CloudPan.Server;
using CloudPan.Server.Data;
using CloudPan.Server.Models;
using CloudPan.Server.Services;
using CloudPan.Shared;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using IOFile = System.IO.File;

namespace CloudPan.Server.Controllers;

/// <summary>
/// 文件操作 API（partial 拆分）：删除、移动、创建文件夹、搜索。
/// </summary>
public partial class FilesController
{

    /// <summary>
    /// POST /api/files/delete — 删除文件或文件夹（递归）。
    /// Phase 1a：检查 baseVersion 防冲突。
    /// </summary>
    [HttpPost("delete")]
    public async Task<IActionResult> Delete([FromBody] DeleteRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Path))
        {
            return this.Error(HttpErrorCode.BAD_REQUEST, "path 参数缺失", "请提供文件路径");
        }

        string? pathErr = _storage.ValidatePath(request.Path);
        if (pathErr != null)
        {
            return this.Error(HttpErrorCode.BAD_REQUEST, pathErr, "路径格式不正确");
        }

        var entry = await _index.GetByPathAsync(request.Path);
        if (entry == null)
        {
            return this.Error(HttpErrorCode.NOT_FOUND, $"文件不存在: {request.Path}", "文件未找到");
        }

        // 冲突检测
        if (request.BaseVersion > 0 && entry.Version > request.BaseVersion)
        {
            // 写入审计日志（删除冲突）
            string delConflictDeviceId = HttpContext.Items["DeviceId"] as string ?? "unknown";
            await _syncLog.LogAsync(request.Path, SyncOperation.Delete, delConflictDeviceId, LogResult.Conflict,
                $"服务端 v{entry.Version}，客户端 v{request.BaseVersion}");

            return this.Error(HttpErrorCode.CONFLICT,
                $"版本冲突：客户端基于 v{request.BaseVersion}，服务端当前 v{entry.Version}",
                "文件已被其他设备修改，请刷新后重试",
                detail: $"currentVersion={entry.Version}, baseVersion={request.BaseVersion}");
        }

        bool isDirectory = entry.Type == (int)FileType.Directory;

        // 先删除 DB 条目（失败则抛异常，文件保持原样，索引与 FS 一致）
        await _index.DeleteAsync(request.Path, isDirectory);

        // 再移入回收站（FS）；失败则物理删除兜底，避免孤儿文件
        try
        {
            TrashController.MoveToTrash(_storage, request.Path, isDirectory);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "移入回收站失败，尝试物理删除: {Path}", request.Path);
            try
            {
                if (isDirectory) { _storage.DeleteDirectory(request.Path); }
                else { _storage.Delete(request.Path); }
            }
            catch (Exception ex2) { _logger.LogWarning(ex2, "物理删除失败: {Path}", request.Path); }
        }

        int newVersion = await _version.NextVersionAsync();

        // 写入审计日志（删除成功）
        string delDeviceId = HttpContext.Items["DeviceId"] as string ?? "unknown";
        await _syncLog.LogAsync(request.Path, SyncOperation.Delete, delDeviceId, LogResult.Success);

        // WebSocket 广播
        await _wsHandler.BroadcastFileDeletedAsync(request.Path, delDeviceId);

        return Ok(new
        {
            data = new { path = request.Path, deletedVersion = newVersion }
        });
    }

    /// <summary>
    /// POST /api/files/move — 移动或重命名文件/文件夹。
    /// </summary>
    [HttpPost("move")]
    public async Task<IActionResult> Move([FromBody] MoveRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.OldPath) || string.IsNullOrWhiteSpace(request.NewPath))
        {
            return this.Error(HttpErrorCode.BAD_REQUEST, "oldPath 和 newPath 参数缺失", "请提供源路径和目标路径");
        }

        string? err1 = _storage.ValidatePath(request.OldPath);
        string? err2 = _storage.ValidatePath(request.NewPath);
        if (err1 != null || err2 != null)
        {
            return this.Error(HttpErrorCode.BAD_REQUEST, (err1 ?? err2)!, "路径格式不正确"); // 上方已校验至少一个非空
        }

        var entry = await _index.GetByPathAsync(request.OldPath);
        if (entry == null)
        {
            return this.Error(HttpErrorCode.NOT_FOUND, $"文件不存在: {request.OldPath}", "文件未找到");
        }

        bool isDirectory = entry.Type == (int)FileType.Directory;

        // 先执行 DB 索引更新（不含审计日志），成功后再移动物理文件，最后写入审计日志
        int newVersion = await _version.NextVersionAsync();
        await _index.MoveAsync(request.OldPath, request.NewPath, newVersion, isDirectory);

        // 移动物理文件——失败时回滚 DB 索引
        try
        {
            if (isDirectory)
            {
                string src = _storage.GetAbsolutePath(request.OldPath);
                string dst = _storage.GetAbsolutePath(request.NewPath);
                if (Directory.Exists(src))
                {
                    Directory.Move(src, dst);
                }
            }
            else
            {
                _storage.Move(request.OldPath, request.NewPath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "物理文件移动失败，正在回滚 DB 索引: {Old} → {New}", request.OldPath, request.NewPath);
            // 回滚 DB：将索引移回原路径
            try { await _index.MoveAsync(request.NewPath, request.OldPath, newVersion, isDirectory); }
            catch (Exception rollbackEx) { _logger.LogError(rollbackEx, "回滚 DB 索引失败——需手动修复: {Old}", request.OldPath); }
            return this.Error(HttpErrorCode.INTERNAL_ERROR, $"文件移动失败: {ex.Message}", "文件移动失败，请检查磁盘空间和权限");
        }

        // 物理文件移动成功后写入审计日志（避免物理移动失败时日志仍显示"成功"）
        string moveDeviceId = HttpContext.Items["DeviceId"] as string ?? "unknown";
        await _syncLog.LogAsync(request.NewPath, SyncOperation.Rename, moveDeviceId, LogResult.Success,
            $"重命名: {request.OldPath} → {request.NewPath}");

        // WebSocket 广播
        await _wsHandler.BroadcastFileRenamedAsync(request.OldPath, request.NewPath, moveDeviceId);

        return Ok(new
        {
            data = new { oldPath = request.OldPath, newPath = request.NewPath, version = newVersion }
        });
    }

    /// <summary>
    /// POST /api/files/mkdir — 创建文件夹。
    /// </summary>
    [HttpPost("mkdir")]
    public async Task<IActionResult> Mkdir([FromBody] MkdirRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Path))
        {
            return this.Error(HttpErrorCode.BAD_REQUEST, "path 参数缺失", "请提供文件夹路径");
        }

        string dirPath = request.Path;
        string? pathErr = _storage.ValidatePath(dirPath);
        if (pathErr != null)
        {
            return this.Error(HttpErrorCode.BAD_REQUEST, pathErr, "路径格式不正确");
        }

        if (!dirPath.StartsWith('/'))
        {
            dirPath = "/" + dirPath;
        }

        // 确保以 / 结尾
        if (!dirPath.EndsWith('/'))
        {
            dirPath += "/";
        }

        try
        {
            _storage.CreateDirectory(dirPath);
            int dirVersion = await _version.NextVersionAsync();
            await _index.CreateDirectoryAsync(dirPath, dirVersion);
            return Ok(new { data = new { path = dirPath } });
        }
        catch (InvalidOperationException)
        {
            return this.Error(HttpErrorCode.CONFLICT, $"路径已存在: {request.Path}", "该路径已存在，请更换名称");
        }
    }

    /// <summary>
    /// GET /api/files/search?q=... — 按文件名搜索。
    /// </summary>
    [HttpGet("search")]
    public async Task<IActionResult> Search([FromQuery] string q, [FromQuery] int limit = 50)
    {
        if (string.IsNullOrWhiteSpace(q) || q.Length < 2)
        {
            return this.Error(HttpErrorCode.BAD_REQUEST, "搜索关键词至少 2 个字符", "搜索关键词至少需要 2 个字符");
        }

        var results = await _index.SearchAsync(q, Math.Min(limit, 200));
        return Ok(new { data = results });
    }
}
