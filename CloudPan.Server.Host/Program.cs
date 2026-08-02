using CloudPan.Infrastructure.Configuration;
using CloudPan.Infrastructure.Persistence;
using CloudPan.Infrastructure.Storage;
using CloudPan.Server.Core;
using CloudPan.Server.Host.Hosting;
using CloudPan.Server.Host.Middleware;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Serilog;
using Serilog.Events;

var builder = WebApplication.CreateBuilder(args);

// Windows Service 支持
builder.Host.UseWindowsService();

// 同步根目录 + HTTP 端口（优先级：CLI 参数 → server-settings.json → 默认值）
(string syncRoot, int httpPort) = StartupSettingsResolver.Resolve(
    builder.Configuration.GetValue<string>("SyncRoot"),
    builder.Configuration.GetValue<int?>("Port"),
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "CloudPan"));

// Serilog 结构化日志（日志目录创建失败则终止启动）
string logDir = Path.Combine(syncRoot, ".cloudpan", "logs");
try
{
    Directory.CreateDirectory(logDir);
}
catch (Exception ex)
{
    ShowError("CloudPan — 启动失败",
        $"日志目录创建失败:\n{logDir}\n\n原因: {ex.Message}\n\n请检查同步根目录路径是否有效、磁盘是否可用。");
    return;
}

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

string dbPath = Path.Combine(syncRoot, ".cloudpan", "server.db");

// Kestrel HTTP 监听（Phase 0 未启用 TLS；端口可配置，重启生效）
builder.WebHost.UseKestrel(options =>
{
    options.ListenAnyIP(httpPort);
});

// ============ 依赖注入 ============
builder.Services.AddDbContextFactory<CloudPanDbContext>(options =>
{
    options.UseSqlite($"Data Source={dbPath}");
});

// 领域服务（Core/Infrastructure）
builder.Services.AddSingleton(syncRoot); // 供 BackgroundHostedService/TokenService 注入
builder.Services.AddSingleton(typeof(int), httpPort); // 供 UDP 广播/设置页/托盘获取"当前生效端口"
builder.Services.AddSingleton<IFileStorageService>(new FileStorageService(syncRoot));
builder.Services.AddSingleton<IFileIndexService, FileIndexService>();
builder.Services.AddSingleton<IVersionService, VersionService>();
builder.Services.AddSingleton<IUploadService, UploadService>();
builder.Services.AddSingleton<ISyncLogService, SyncLogService>();
builder.Services.AddSingleton<IWebSocketHandler, WebSocketHandler>();
// 文件类领域服务（F-02 下沉：事务与 DB+FS 一致性收敛进 Server.Core）
builder.Services.AddSingleton<ISharingService, SharingService>();
builder.Services.AddSingleton<ITrashService, TrashService>();
builder.Services.AddSingleton<IVersionHistoryService, VersionHistoryService>();
builder.Services.AddSingleton<IThumbnailService, ThumbnailService>();
builder.Services.AddSingleton<IFileOperationService, FileOperationService>();
builder.Services.AddSingleton<IChunkedUploadService, ChunkedUploadService>();
builder.Services.AddSingleton<IServerStatusService, ServerStatusService>();
builder.Services.AddSingleton<ISettingsService, SettingsService>();
builder.Services.AddSingleton<ITokenService, TokenService>();

builder.Services.AddMemoryCache();
builder.Services.AddControllers();
builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = 50_000_000; // 50MB
});

// 后台任务（R-A6：定时任务用 IHostedService）
builder.Services.AddHostedService<BackgroundHostedService>();
builder.Services.AddHostedService<UdpDiscoveryHostedService>();

var app = builder.Build();

// ============ 初始化（建库/完整性/种子/Token） ============
DatabaseInitializer.Initialize(app.Services, syncRoot);

// ============ 中间件管道 ============
// UseRouting 需在管道最前面——后续中间件通过 context.GetEndpoint() 读取端点元数据（EndpointAuthAttribute）
app.UseRouting();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });
app.UseRequestId();
app.UseGlobalExceptionHandler();
// TokenAuth 必须在 RateLimit 之前——RateLimit 依赖 TokenAuth 设置的 context.Items["DeviceId"]
app.UseTokenAuth();
app.UseRateLimit();

// WebSocket /ws（消息级认证：deviceId/token 在首条 auth 消息中解析，见 WebSocketHandler）
app.Map("/ws", async (HttpContext context, IWebSocketHandler handler) =>
{
    if (context.WebSockets.IsWebSocketRequest)
    {
        var socket = await context.WebSockets.AcceptWebSocketAsync();
        await handler.HandleConnectionAsync(socket);
    }
    else
    {
        context.Response.StatusCode = 400;
    }
});

app.MapControllers();

// 启动信息
app.Lifetime.ApplicationStarted.Register(() =>
{
    var logger = app.Services.GetRequiredService<ILogger<Program>>();
    logger.LogInformation("CloudPan Server v1.0.0 启动，同步目录: {SyncRoot}", syncRoot);
});

app.Lifetime.ApplicationStopped.Register(() => Log.CloseAndFlush());

// ============ 运行（headless / Windows Service / tray GUI） ============
await TrayAppRunner.RunAsync(app, args);
return;

/// <summary>显示错误对话框（仅交互模式），同时写入 Console.Error。非交互模式省略对话框。</summary>
static void ShowError(string title, string message)
{
    Console.Error.WriteLine($"[{title}] {message}");
    if (Environment.UserInteractive)
    {
        try { System.Windows.Forms.MessageBox.Show(message, title, System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Error); }
        catch { /* 非交互环境忽略 */ }
    }
}

/// <summary>使测试项目可见。</summary>
public partial class Program { }
