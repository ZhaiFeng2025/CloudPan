using Microsoft.EntityFrameworkCore;

namespace CloudPan.Client.Models;

/// <summary>
/// 客户端本地 SQLite 数据库。
/// 存储传输队列、远程快照、同步游标。
/// schema 由 EF Core Migrations 管理（初始迁移幂等，兼容 EnsureCreated 时代的旧库，T-008）。
/// </summary>
public class ClientDbContext : DbContext
{
    public DbSet<SyncQueueItem> SyncQueue => Set<SyncQueueItem>();
    public DbSet<RemoteSnapshot> RemoteSnapshots => Set<RemoteSnapshot>();
    public DbSet<SyncCursorState> SyncCursor => Set<SyncCursorState>();

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

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<SyncQueueItem>(e =>
        {
            e.HasKey(q => q.Id);
            e.HasIndex(q => new { q.Priority, q.CreatedAt }).IsDescending(true, false);
        });

        model.Entity<RemoteSnapshot>(e =>
        {
            e.HasKey(s => s.Path);
        });

        model.Entity<SyncCursorState>(e =>
        {
            e.HasKey(c => c.Id);
        });
    }
}

/// <summary>客户端持久化传输队列。</summary>
public class SyncQueueItem
{
    public int Id { get; set; }
    public string FilePath { get; set; } = "";
    public int Operation { get; set; } // 0=Upload, 1=Download, 2=Delete, 3=Rename
    public int Priority { get; set; }
    public int? BaseVersion { get; set; }
    public long? FileSize { get; set; }
    public int RetryCount { get; set; }
    public string? LastError { get; set; }
    public string? TargetPath { get; set; } // 重命名操作的目标路径
    public string CreatedAt { get; set; } = DateTime.UtcNow.ToString("O");
}

/// <summary>服务端文件树快照。</summary>
public class RemoteSnapshot
{
    public string Path { get; set; } = "";
    public int Type { get; set; }
    public string? Hash { get; set; }
    public long Size { get; set; }
    public int Version { get; set; }
    public int State { get; set; }
}

/// <summary>同步游标（单行表）。</summary>
public class SyncCursorState
{
    public int Id { get; set; } = 1;
    public int LastMaxVersion { get; set; }
    public string? LastSyncAt { get; set; }
}
