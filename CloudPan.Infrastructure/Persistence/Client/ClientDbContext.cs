using CloudPan.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;

namespace CloudPan.Infrastructure.Persistence.Client;

/// <summary>
/// 客户端本地 SQLite 数据库。
/// 存储传输队列、远程快照、同步游标。
/// 实体类型由 Generated/ClientEntities.g.cs 从 shared-spec.json 生成（规则 0 契约驱动），
/// 此处仅声明 DbSet；[Table]/[Key]/[Index] 映射由生成器输出，禁止手工重复定义。
/// schema 由 EF Core Migrations 管理（初始迁移幂等，兼容 EnsureCreated 时代的旧库，T-008）。
/// WAL/PRAGMA 策略经 SqlitePragma.EnsureWAL 单一实现（T-068，与服务端对齐复用）。
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

    protected override void OnConfiguring(DbContextOptionsBuilder options)
    {
        if (!options.IsConfigured)
        {
            options.UseSqlite($"Data Source={_dbPath}");
        }
    }
}
