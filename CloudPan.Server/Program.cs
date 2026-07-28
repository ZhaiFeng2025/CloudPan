using Microsoft.EntityFrameworkCore;
using CloudPan.Server.Data;
using CloudPan.Server.Models;
using CloudPan.Server.Services;

var builder = WebApplication.CreateBuilder(args);

// ============================================================
// 配置
// ============================================================
var syncRoot = builder.Configuration.GetValue<string>("SyncRoot")
               ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "CloudPan");

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
builder.Services.AddSingleton(new FileStorageService(syncRoot));
builder.Services.AddSingleton<FileIndexService>();
builder.Services.AddSingleton<VersionService>();

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
var storage = app.Services.GetRequiredService<FileStorageService>();
storage.EnsureSyncRootExists();

using (var scope = app.Services.CreateScope())
{
    var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<CloudPanDbContext>>();
    await using var db = await dbFactory.CreateDbContextAsync();

    // 确保数据库已创建
    await db.Database.EnsureCreatedAsync();

    // 设置 WAL 模式
    await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
    await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys=ON;");
    await db.Database.ExecuteSqlRawAsync("PRAGMA synchronous=NORMAL;");

    // 初始化预定义配置（如果不存在）
    if (!await db.AppConfigs.AnyAsync(c => c.Key == "global_version"))
    {
        db.AppConfigs.Add(new AppConfig { Key = "global_version", Value = "0" });
        await db.SaveChangesAsync();
    }
}

// ============================================================
// 中间件管道
// ============================================================

// Phase 0：不启用认证中间件
// Phase 1a：添加 Token 认证中间件

app.MapControllers();

// 启动日志
var urls = "http://0.0.0.0:8443";
Console.WriteLine("========================================");
Console.WriteLine($"  CloudPan Server v0.1.0");
Console.WriteLine($"  同步根: {syncRoot}");
Console.WriteLine($"  数据库: {dbPath}");
Console.WriteLine($"  监听:   {urls}");
Console.WriteLine("========================================");

app.Run();
