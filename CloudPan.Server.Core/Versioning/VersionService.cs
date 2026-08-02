using CloudPan.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CloudPan.Server.Core;

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

        // 使用显式事务包装 UPDATE+SELECT，确保原子递增
        await using var tx = await db.Database.BeginTransactionAsync();

        // 如果 global_version 行不存在则先创建
        await db.Database.ExecuteSqlRawAsync(
            "INSERT OR IGNORE INTO AppConfig(Key, Value) VALUES('global_version', '0')");

        // 原子递增（其他连接在提交前只能看到旧值）
        await db.Database.ExecuteSqlRawAsync(
            "UPDATE AppConfig SET Value = CAST(Value AS INTEGER) + 1 WHERE Key = 'global_version'");

        // 在同一事务中读取最新值
        var config = await db.AppConfigs.FindAsync("global_version");
        int result = config != null ? int.Parse(config.Value) : 1;

        await tx.CommitAsync();

        return result;
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
