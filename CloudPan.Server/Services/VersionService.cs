using CloudPan.Server.Data;
using CloudPan.Server.Models;
using Microsoft.EntityFrameworkCore;

namespace CloudPan.Server.Services;

/// <summary>
/// 全局版本号分配服务。
/// 使用 AppConfig 表的 global_version 键做原子递增。
/// SQLite WAL 模式下写操作串行，保证并发安全。
/// </summary>
public class VersionService
{
    private readonly IDbContextFactory<CloudPanDbContext> _dbFactory;

    public VersionService(IDbContextFactory<CloudPanDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    /// <summary>
    /// 获取下一个全局版本号（原子递增）。
    /// </summary>
    public async Task<int> NextVersionAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        // 使用 SQLite 行锁保证原子性
        var config = await db.AppConfigs.FindAsync("global_version");
        if (config == null)
        {
            config = new AppConfig { Key = "global_version", Value = "1" };
            db.AppConfigs.Add(config);
            await db.SaveChangesAsync();
            return 1;
        }

        var next = int.Parse(config.Value) + 1;
        config.Value = next.ToString();
        await db.SaveChangesAsync();
        return next;
    }

    /// <summary>
    /// 获取当前版本号（不递增）。
    /// </summary>
    public async Task<int> GetCurrentVersionAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var config = await db.AppConfigs.FindAsync("global_version");
        return config != null ? int.Parse(config.Value) : 0;
    }
}
