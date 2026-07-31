using CloudPan.Server;
using CloudPan.Server.Data;
using CloudPan.Server.Models;
using CloudPan.Server.Services;
using CloudPan.Shared;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using IOFile = System.IO.File;
using IOFileInfo = System.IO.FileInfo;

namespace CloudPan.Server.Controllers;

/// <summary>
/// 版本历史 API——浏览历史版本、回滚到指定版本。
/// </summary>
[ApiController]
[Route("api/versions")]
[EndpointAuth(AuthMode.Token)]
public class VersionsController : ControllerBase
{
    private readonly IDbContextFactory<CloudPanDbContext> _dbFactory;
    private readonly IFileStorageService _storage;
    private readonly IFileIndexService _index;
    private readonly IVersionService _version;
    private readonly ISyncLogService _syncLog;
    private readonly IWebSocketHandler _wsHandler;

    public VersionsController(
        IDbContextFactory<CloudPanDbContext> dbFactory,
        IFileStorageService storage,
        IFileIndexService index,
        IVersionService version,
        ISyncLogService syncLog,
        IWebSocketHandler wsHandler)
    {
        _dbFactory = dbFactory;
        _storage = storage;
        _index = index;
        _version = version;
        _syncLog = syncLog;
        _wsHandler = wsHandler;
    }

    /// <summary>
    /// GET /api/versions?path=... — 获取文件的所有历史版本。
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetVersions([FromQuery] string path, [FromQuery] int limit = 10)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return this.Error(HttpErrorCode.BAD_REQUEST, "path 参数缺失", "缺少文件路径参数");
        }

        await using var db = await _dbFactory.CreateDbContextAsync();
        var versions = await db.VersionRecords
            .Where(v => v.FilePath == path)
            .OrderByDescending(v => v.Version)
            .Take(Math.Min(limit, 50))
            .Select(v => new
            {
                version = v.Version,
                hash = v.Hash,
                size = v.Size,
                timestamp = v.Timestamp,
                deviceId = v.DeviceId,
                restoredFromVersion = v.RestoredFromVersion
            })
            .ToListAsync();

        return Ok(new { data = versions });
    }

    /// <summary>
    /// POST /api/versions/restore — 回滚到指定历史版本。
    /// 回滚本身会先存档当前版本，再用历史文件覆盖。
    /// </summary>
    [HttpPost("restore")]
    public async Task<IActionResult> Restore([FromBody] RestoreRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.FilePath))
        {
            return this.Error(HttpErrorCode.BAD_REQUEST, "filePath 参数缺失", "缺少文件路径参数");
        }

        await using var db = await _dbFactory.CreateDbContextAsync();

        // 查找目标版本
        var targetVersion = await db.VersionRecords
            .FirstOrDefaultAsync(v => v.FilePath == request.FilePath && v.Version == request.Version);
        if (targetVersion == null)
        {
            return this.Error(HttpErrorCode.NOT_FOUND, $"版本不存在: v{request.Version}", "指定的历史版本不存在");
        }

        // 检查历史文件
        string versionFilePath = Path.Combine(
            Path.GetDirectoryName(_storage.GetAbsolutePath(request.FilePath))!,
            ".cloudpan", ".versions", targetVersion.StoragePath);
        if (!IOFile.Exists(versionFilePath))
        {
            return this.Error(HttpErrorCode.NOT_FOUND, "历史版本文件已丢失", "历史版本文件已丢失，无法恢复");
        }

        var currentEntry = await _index.GetByPathAsync(request.FilePath);
        if (currentEntry == null)
        {
            return this.Error(HttpErrorCode.NOT_FOUND, $"文件不存在: {request.FilePath}", "文件不存在，无法恢复");
        }

        // 1. 存档当前版本
        string deviceId = HttpContext.Items["DeviceId"] as string ?? "unknown";
        string storagePath = await _storage.StoreVersionAsync(request.FilePath, currentEntry.Version);

        db.VersionRecords.Add(new VersionRecord
        {
            FilePath = request.FilePath,
            Version = currentEntry.Version,
            Hash = currentEntry.CurrentHash ?? "",
            Size = currentEntry.CurrentSize,
            StoragePath = storagePath,
            Timestamp = DateTime.UtcNow.ToString("O"),
            DeviceId = deviceId
        });

        // 2. 用历史文件覆盖当前文件
        string targetPath = _storage.GetAbsolutePath(request.FilePath);
        string tmpPath = targetPath + ".tmp";
        using (var srcStream = IOFile.OpenRead(versionFilePath))
        using (var dstStream = IOFile.Create(tmpPath))
        {
            await srcStream.CopyToAsync(dstStream);
            await dstStream.FlushAsync();
        }
        IOFile.Move(tmpPath, targetPath, overwrite: true);

        // 3. 计算哈希并更新索引
        int newVersion = await _version.NextVersionAsync();
        string hash = await _storage.ComputeHashAsync(targetPath);
        long size = new IOFileInfo(targetPath).Length;

        await _index.UpsertFileAsync(
            request.FilePath, FileType.File, hash, size,
            DateTime.UtcNow.ToString("O"), newVersion);

        // 4. 记录回滚来源
        db.VersionRecords.Add(new VersionRecord
        {
            FilePath = request.FilePath,
            Version = newVersion,
            Hash = hash,
            Size = size,
            StoragePath = storagePath, // 当前版本的文件已在步骤1中存档
            Timestamp = DateTime.UtcNow.ToString("O"),
            DeviceId = deviceId,
            RestoredFromVersion = request.Version
        });

        await db.SaveChangesAsync();

        // 写入审计日志（回滚成功）
        string restoreDeviceId = HttpContext.Items["DeviceId"] as string ?? "unknown";
        await _syncLog.LogAsync(request.FilePath, SyncOperation.Restore, restoreDeviceId, LogResult.Success,
            $"回滚到 v{request.Version}");

        // WebSocket 广播
        await _wsHandler.BroadcastFileChangedAsync(request.FilePath, newVersion, restoreDeviceId);

        return Ok(new
        {
            data = new
            {
                path = request.FilePath,
                version = newVersion,
                hash,
                size,
                restoredFromVersion = request.Version
            }
        });
    }
}

/// <summary>将文件恢复至指定历史版本的请求。</summary>
public record RestoreRequest(string FilePath, int Version);
