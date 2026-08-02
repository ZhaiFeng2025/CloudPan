using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace CloudPan.Client.Core.Models;

/// <summary>
/// dotnet ef 设计时工厂：仅供生成/执行 EF Migrations 使用，不参与运行时。
/// 迁移 SQL 与模型相关，与连接字符串指向的库文件无关。
/// </summary>
public class ClientDesignTimeDbContextFactory : IDesignTimeDbContextFactory<ClientDbContext>
{
    public ClientDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<ClientDbContext>()
            .UseSqlite("Data Source=migrations-design.db")
            .Options;
        return new ClientDbContext(options);
    }
}
