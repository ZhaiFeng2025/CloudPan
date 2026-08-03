using Microsoft.EntityFrameworkCore;

namespace CloudPan.Infrastructure.Persistence;

/// <summary>
/// SQLite 连接 PRAGMA 策略单一实现（WAL/synchronous/busy_timeout/foreign_keys）。
/// 服务端（DatabaseInitializer）与客户端（ClientDbFactory/ClientBootstrap 建库）共用，
/// 删除两端各自重复实现（T-068，基础设施单一实现）。
/// </summary>
public static class SqlitePragma
{
    /// <summary>对当前连接应用 WAL 模式 + synchronous=NORMAL + busy_timeout + 外键约束。</summary>
    public static void EnsureWAL(DbContext db)
    {
        db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
        db.Database.ExecuteSqlRaw("PRAGMA synchronous=NORMAL;");
        db.Database.ExecuteSqlRaw("PRAGMA busy_timeout=5000;");
        db.Database.ExecuteSqlRaw("PRAGMA foreign_keys=ON;");
    }
}
