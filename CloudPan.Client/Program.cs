using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using CloudPan.Client.Models;
using CloudPan.Client.Services;
using CloudPan.Client.UI;
using Serilog;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace CloudPan.Client;

public static class Program
{
    public static string SyncRoot { get; private set; } = "";
    private static string ServerUrl { get; set; } = "http://localhost:8443";

    [STAThread]
    public static void Main(string[] args)
    {
        // 1. 解析命令行参数
        if (args.Length >= 1) ServerUrl = args[0];
        SyncRoot = args.Length >= 2
            ? args[1]
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "CloudPan");

        Directory.CreateDirectory(SyncRoot);
        var dbPath = Path.Combine(SyncRoot, ".cloudpan", "client.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

        // 2. Serilog 日志初始化
        var logDir = Path.Combine(SyncRoot, ".cloudpan", "logs");
        Directory.CreateDirectory(logDir);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Console(
                outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
            .WriteTo.File(
                Path.Combine(logDir, "client-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        // 3. DI 容器
        var services = new ServiceCollection();

        services.AddLogging(b => b.AddSerilog(dispose: true));

        // 配置
        services.AddSingleton(new SyncConfig { SyncRoot = SyncRoot, ServerUrl = ServerUrl });

        // 数据库（DbContextFactory 确保并发安全）
        services.AddSingleton<IDbContextFactory<ClientDbContext>>(_ => new ClientDbFactory(dbPath));
        EnsureDbCreated(dbPath);

        // HTTP 客户端
        services.AddSingleton<IApiClient>(new ApiClient(ServerUrl));

        // 服务层
        services.AddSingleton<SyncEngine>();
        services.AddSingleton<FileWatcherService>();

        var provider = services.BuildServiceProvider();

        // 4. 健康检查（同步执行，不阻塞启动即可）
        var apiClient = provider.GetRequiredService<IApiClient>();
        var loggerFactory = provider.GetRequiredService<ILoggerFactory>();
        var logger = loggerFactory.CreateLogger("CloudPan.Client");

        var connected = apiClient.HealthCheckAsync().GetAwaiter().GetResult();
        if (!connected)
        {
            logger.LogWarning("无法连接到服务端 {ServerUrl}", ServerUrl);
            logger.LogInformation("客户端将以离线模式启动，连接恢复后自动同步");
        }
        else
        {
            logger.LogInformation("已连接到服务端 {ServerUrl}", ServerUrl);
        }

        // 5. 启动文件监控
        var watcher = provider.GetRequiredService<FileWatcherService>();
        watcher.Start();

        // 6. 启动同步引擎 + 托盘应用
        var engine = provider.GetRequiredService<SyncEngine>();
        ApplicationConfiguration.Initialize();
        Application.Run(new TrayAppContext(engine));
    }

    private static void EnsureDbCreated(string dbPath)
    {
        using var db = new ClientDbContext(dbPath);
        db.Database.EnsureCreated();
        db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
    }
}

/// <summary>简易 DbContextFactory 实现。</summary>
internal class ClientDbFactory : IDbContextFactory<ClientDbContext>
{
    private readonly string _dbPath;
    public ClientDbFactory(string dbPath) => _dbPath = dbPath;
    public ClientDbContext CreateDbContext() => new(_dbPath);
}
