using CloudPan.Infrastructure.Models;
using CloudPan.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CloudPan.Tests.Infrastructure;

/// <summary>
/// 测试基类——为每个测试提供隔离的临时目录和 SQLite 数据库。
/// 实现 IDisposable，xUnit 在测试结束后自动调用 Dispose 清理。
/// </summary>
public abstract class TestBase : IDisposable
{
    protected string TempDir { get; }

    protected TestBase()
    {
        TempDir = Path.Combine(Path.GetTempPath(), $"CloudPanTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(TempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(TempDir))
        {
            try { Directory.Delete(TempDir, recursive: true); }
            catch { /* 资源可能仍在使用，尽力清理 */ }
        }
    }

    /// <summary>
    /// 创建指向独立临时 SQLite 文件的服务端 DbContextFactory。
    /// 自动调用 EnsureCreated 并写入种子数据。
    /// </summary>
    protected IDbContextFactory<CloudPanDbContext> CreateServerDbFactory()
    {
        string dbPath = Path.Combine(TempDir, "test.db");
        var options = new DbContextOptionsBuilder<CloudPanDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Options;

        // 初始化数据库 + 种子数据
        using CloudPanDbContext db = new CloudPanDbContext(options);
        db.Database.EnsureCreated();

        db.Devices.Add(new Device
        {
            Id = "server",
            Name = "服务端",
            Person = null,
            LastSeen = DateTime.UtcNow.ToString("O"),
            Online = 1,
            RegisteredAt = DateTime.UtcNow.ToString("O")
        });

        db.AppConfigs.Add(new AppConfig { Key = "global_version", Value = "0" });
        db.SaveChanges();

        return new TestServerDbFactory(options);
    }

    /// <summary>
    /// 简易 DbContextFactory 实现——每个测试创建独立实例。
    /// </summary>
    private sealed class TestServerDbFactory : IDbContextFactory<CloudPanDbContext>
    {
        private readonly DbContextOptions<CloudPanDbContext> _options;
        public TestServerDbFactory(DbContextOptions<CloudPanDbContext> options) => _options = options;
        public CloudPanDbContext CreateDbContext() => new(_options);
    }
}
