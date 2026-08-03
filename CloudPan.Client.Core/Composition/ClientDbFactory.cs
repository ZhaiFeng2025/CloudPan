using CloudPan.Client.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace CloudPan.Client.Core.Composition;

/// <summary>运行时 ClientDbContext 工厂：每次创建连接并确保 WAL/busy_timeout/外键等 PRAGMA 已设置。</summary>
internal class ClientDbFactory : IDbContextFactory<ClientDbContext>
{
    private readonly string _dbPath;

    public ClientDbFactory(string dbPath) => _dbPath = dbPath;

    public ClientDbContext CreateDbContext()
    {
        ClientDbContext db = new ClientDbContext(_dbPath);
        db.EnsureWAL();
        return db;
    }
}
