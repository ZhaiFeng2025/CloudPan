using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using CloudPan.Server.Data;
using CloudPan.Server.Middleware;
using CloudPan.Server.Models;
using CloudPan.Server.Services;
using Serilog;
using Serilog.Events;

var builder = WebApplication.CreateBuilder(args);

// ============================================================
// 配置
// ============================================================
var syncRoot = builder.Configuration.GetValue<string>("SyncRoot")
               ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "CloudPan");

// Serilog 结构化日志
var logDir = Path.Combine(syncRoot, ".cloudpan", "logs");
Directory.CreateDirectory(logDir);

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
    .WriteTo.Console(
        outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .WriteTo.File(
        Path.Combine(logDir, "server-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 7,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

builder.Host.UseSerilog();

var dbPath = Path.Combine(syncRoot, ".cloudpan", "server.db");

builder.WebHost.UseKestrel(options =>
{
    options.ListenAnyIP(8443); // HTTP only for Phase 0
});

// ============================================================
// 依赖注入
// ============================================================

// SQLite + EF Core（使用 DbContextFactory 以支持并发安全）
builder.Services.AddDbContextFactory<CloudPanDbContext>(options =>
{
    options.UseSqlite($"Data Source={dbPath}");
});

// 服务层
builder.Services.AddSingleton<IFileStorageService>(new FileStorageService(syncRoot));
builder.Services.AddSingleton<IFileIndexService, FileIndexService>();
builder.Services.AddSingleton<IVersionService, VersionService>();

// Controller
builder.Services.AddControllers();

// 大文件上传支持
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = 50_000_000; // 50MB
});

var app = builder.Build();

// ============================================================
// 初始化
// ============================================================

// 确保目录和数据库存在
var storage = app.Services.GetRequiredService<IFileStorageService>();
storage.EnsureSyncRootExists();

using (var scope = app.Services.CreateScope())
{
    var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<CloudPanDbContext>>();
    await using var db = await dbFactory.CreateDbContextAsync();

    // 确保数据库已创建
    await db.Database.EnsureCreatedAsync();

    // 设置 WAL 模式 + 启用外键约束（VersionRecord 在 FileEntry 之前删除）
    await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
    await db.Database.ExecuteSqlRawAsync("PRAGMA synchronous=NORMAL;");
    await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys=ON;");

    // 种子："server" 设备（用于 VersionRecord.DeviceId FK）
    if (!await db.Devices.AnyAsync(d => d.Id == "server"))
    {
        db.Devices.Add(new Device
        {
            Id = "server",
            Name = "服务端",
            Person = null,
            LastSeen = DateTime.UtcNow.ToString("O"),
            Online = 1,
            RegisteredAt = DateTime.UtcNow.ToString("O")
        });
    }

    // 初始化预定义配置（如果不存在）
    if (!await db.AppConfigs.AnyAsync(c => c.Key == "global_version"))
    {
        db.AppConfigs.Add(new AppConfig { Key = "global_version", Value = "0" });
    }

    // 首次启动时生成家庭共享 Token（仅输出一次）
    if (!await db.AppConfigs.AnyAsync(c => c.Key == "token_hash"))
    {
        // 优先从 app 配置读取（支持测试注入），否则自动生成
        var presetToken = app.Configuration.GetValue<string>("CloudPan:Token");
        string token;
        if (!string.IsNullOrEmpty(presetToken))
        {
            token = presetToken;
        }
        else
        {
            // 生产环境：自动生成 64 字符随机 Token
            token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine();
            Console.WriteLine("╔══════════════════════════════════════════════════════════════╗");
            Console.WriteLine("║  家庭共享 Token（仅显示一次，请妥善保存）                    ║");
            Console.WriteLine($"║  {token}  ║");
            Console.WriteLine("╚══════════════════════════════════════════════════════════════╝");
            Console.WriteLine();
            Console.ResetColor();
        }
        var tokenHash = Convert.ToHexString(SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
        db.AppConfigs.Add(new AppConfig { Key = "token_hash", Value = tokenHash });
    }

    await db.SaveChangesAsync();
}

// ============================================================
// 中间件管道
// ============================================================

// 中间件管道（从外到内：异常 → 速率 → 认证）
app.UseGlobalExceptionHandler();
app.UseRateLimit();
app.UseTokenAuth();

app.MapControllers();

// 启动日志
app.Lifetime.ApplicationStarted.Register(() =>
{
    var logger = app.Services.GetRequiredService<ILogger<Program>>();
    logger.LogInformation("CloudPan Server v0.1.0 启动");
    logger.LogInformation("同步根: {SyncRoot}", syncRoot);
    logger.LogInformation("监听: http://0.0.0.0:8443");
});

app.Lifetime.ApplicationStopped.Register(Log.CloseAndFlush);

app.Run();

/// <summary>
/// 使 WebApplicationFactory&lt;Program&gt; 在集成测试中可用。
/// 顶层语句隐式生成的 Program 类为 internal，此声明将其扩展为 public partial。
/// </summary>
public partial class Program { }
