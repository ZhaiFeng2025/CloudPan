using CloudPan.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CloudPan.Infrastructure.Persistence.Client;

/// <summary>运行时 ClientDbContext 工厂：每次创建连接并应用 WAL/busy_timeout/外键等 PRAGMA（单一实现 SqlitePragma，T-068）。</summary>
public class ClientDbFactory : IDbContextFactory<ClientDbContext>
{
    private readonly string _dbPath;

    public ClientDbFactory(string dbPath) => _dbPath = dbPath;

    public ClientDbContext CreateDbContext()
    {
        ClientDbContext db = new ClientDbContext(_dbPath);
        SqlitePragma.EnsureWAL(db);
        return db;
    }
}
