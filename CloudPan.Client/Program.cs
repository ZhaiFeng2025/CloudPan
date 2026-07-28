using Microsoft.EntityFrameworkCore;
using CloudPan.Client.Models;
using CloudPan.Client.Services;
using CloudPan.Client.UI;

namespace CloudPan.Client;

public static class Program
{
    public static string SyncRoot { get; private set; } = "";
    private static string ServerUrl { get; set; } = "http://localhost:8443";

    [STAThread]
    public static void Main(string[] args)
    {
        // 读取命令行参数
        if (args.Length >= 1) ServerUrl = args[0];
        SyncRoot = args.Length >= 2
            ? args[1]
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "CloudPan");

        Directory.CreateDirectory(SyncRoot);
        var dbPath = Path.Combine(SyncRoot, ".cloudpan", "client.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

        // 初始化
        var dbFactory = CreateDbFactory(dbPath);
        var logger = new ClientLogger();
        var apiClient = new ApiClient(ServerUrl);

        ApplicationConfiguration.Initialize();

        // 先检查服务端连接
        var connected = apiClient.HealthCheckAsync().GetAwaiter().GetResult();
        if (!connected)
        {
            logger.Warn($"无法连接到服务端 {ServerUrl}");
            logger.Info("客户端将以离线模式启动，连接恢复后自动同步");
        }
        else
        {
            logger.Info($"已连接到服务端 {ServerUrl}");
        }

        var engine = new SyncEngine(apiClient, SyncRoot, dbFactory, logger);

        // 启动托盘应用
        Application.Run(new TrayAppContext(engine));
    }

    private static IDbContextFactory<ClientDbContext> CreateDbFactory(string dbPath)
    {
        using var db = new ClientDbContext(dbPath);
        db.Database.EnsureCreated();
        db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");

        return new ClientDbFactory(dbPath);
    }
}

/// <summary>简易 DbContextFactory 实现。</summary>
internal class ClientDbFactory : IDbContextFactory<ClientDbContext>
{
    private readonly string _dbPath;
    public ClientDbFactory(string dbPath) => _dbPath = dbPath;
    public ClientDbContext CreateDbContext() => new(_dbPath);
}
