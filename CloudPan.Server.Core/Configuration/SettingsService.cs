using CloudPan.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CloudPan.Server.Core;

/// <summary>
/// 运行时设置服务实现。读写 AppConfig 键值表（SQLite WAL 串行化保证并发安全）。
/// 写操作用 INSERT OR IGNORE + UPDATE 事务模板（与 VersionService 同款），值一律存 string。
/// </summary>
public sealed class SettingsService : ISettingsService
{
    private readonly IDbContextFactory<CloudPanDbContext> _dbFactory;

    public SettingsService(IDbContextFactory<CloudPanDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<string?> GetAsync(string key)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var config = await db.AppConfigs.FindAsync(key);
        return config?.Value;
    }

    public async Task<string> GetStringAsync(string key, string defaultValue)
        => await GetAsync(key) ?? defaultValue;

    public async Task<int> GetIntAsync(string key, int defaultValue)
    {
        string? value = await GetAsync(key);
        return int.TryParse(value, out int result) ? result : defaultValue;
    }

    public async Task SetStringAsync(string key, string value)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        // 参数化防注入——key/value 来自设置 UI 输入，非常量
        await db.Database.ExecuteSqlRawAsync(
            "INSERT OR IGNORE INTO AppConfig(Key, Value) VALUES({0}, {1})", key, value);
        await db.Database.ExecuteSqlRawAsync(
            "UPDATE AppConfig SET Value = {0} WHERE Key = {1}", value, key);
        await tx.CommitAsync();
    }

    public async Task SetIntAsync(string key, int value)
        => await SetStringAsync(key, value.ToString());
}
