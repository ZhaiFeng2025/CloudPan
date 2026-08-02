using CloudPan.Contract;
using CloudPan.Infrastructure.Models;
using CloudPan.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CloudPan.Server.Core;

/// <summary>
/// 审计日志写入服务。
/// 在文件变更操作后记录 SyncLog，写入失败不阻塞主流程。
/// </summary>
public class SyncLogService : ISyncLogService
{
    private readonly IDbContextFactory<CloudPanDbContext> _dbFactory;
    private readonly ILogger<SyncLogService> _logger;

    public SyncLogService(IDbContextFactory<CloudPanDbContext> dbFactory, ILogger<SyncLogService> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task LogAsync(string filePath, SyncOperation operation, string deviceId,
        LogResult result, string? details = null)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            db.SyncLogs.Add(new SyncLog
            {
                FilePath = filePath,
                Operation = (int)operation,
                DeviceId = deviceId,
                Result = (int)result,
                Details = details,
                CreatedAt = DateTime.UtcNow.ToString("O")
            });
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            // 日志写入失败不阻塞主流程
            _logger.LogWarning(ex, "写入 SyncLog 失败: {Path}", filePath);
        }
    }
}
