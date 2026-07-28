using CloudPan.Server.Data;
using CloudPan.Server.Models;
using Microsoft.EntityFrameworkCore;

namespace CloudPan.Server.Services;

/// <summary>
/// 全局版本号分配服务。
/// 使用 AppConfig 表的 global_version 键做原子递增。
/// SQLite WAL 模式下写操作串行，保证并发安全。
/// </summary>
public class VersionService : IVersionService
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

        // 原子递增：SQLite WAL 模式下写操作串行，UPDATE+SELECT 在单事务内保证原子
        // 如果 global_version 行不存在则先创建
        await db.Database.ExecuteSqlRawAsync(
            "INSERT OR IGNORE INTO AppConfig(Key, Value) VALUES('global_version', '0')");

        await db.Database.ExecuteSqlRawAsync(
            "UPDATE AppConfig SET Value = CAST(Value AS INTEGER) + 1 WHERE Key = 'global_version'");

        // 在同一连接上读取（SQLite 连接是串行的）
        var config = await db.AppConfigs.FindAsync("global_version");
        return config != null ? int.Parse(config.Value) : 1;
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
