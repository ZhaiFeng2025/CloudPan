using Microsoft.EntityFrameworkCore;

namespace CloudPan.Client.Core.Models;

/// <summary>
/// 客户端本地 SQLite 数据库。
/// 存储传输队列、远程快照、同步游标。
/// 实体类型由 Generated/ClientEntities.g.cs 从 shared-spec.json 生成（规则 0 契约驱动），
/// 此处仅声明 DbSet；[Table]/[Key]/[Index] 映射由生成器输出，禁止手工重复定义。
/// schema 由 EF Core Migrations 管理（初始迁移幂等，兼容 EnsureCreated 时代的旧库，T-008）。
/// </summary>
public class ClientDbContext : DbContext
{
    public DbSet<SyncQueue> SyncQueue => Set<SyncQueue>();
    public DbSet<RemoteSnapshot> RemoteSnapshots => Set<RemoteSnapshot>();
    public DbSet<SyncCursor> SyncCursor => Set<SyncCursor>();

    private readonly string? _dbPath;

    public ClientDbContext(string dbPath)
    {
        _dbPath = dbPath;
    }

    public ClientDbContext(DbContextOptions<ClientDbContext> options) : base(options) { }

    private bool _pragmaSet;

    protected override void OnConfiguring(DbContextOptionsBuilder options)
    {
        if (!options.IsConfigured)
        {
            options.UseSqlite($"Data Source={_dbPath}");
        }
    }

    /// <summary>确保当前连接设置了 WAL 模式 + busy_timeout + 外键约束。</summary>
    public void EnsureWAL()
    {
        if (!_pragmaSet)
        {
            Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
            Database.ExecuteSqlRaw("PRAGMA synchronous=NORMAL;");
            Database.ExecuteSqlRaw("PRAGMA busy_timeout=5000;");
            Database.ExecuteSqlRaw("PRAGMA foreign_keys=ON;");
            _pragmaSet = true;
        }
    }
}
