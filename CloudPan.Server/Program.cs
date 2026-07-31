using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using CloudPan.Server.Data;
using CloudPan.Server.Middleware;
using CloudPan.Server.Models;
using CloudPan.Server.Services;
using CloudPan.Server.UI;
using CloudPan.Shared;
using Microsoft.EntityFrameworkCore;
using Serilog;
using Serilog.Events;

var builder = WebApplication.CreateBuilder(args);

// Windows Service 支持
builder.Host.UseWindowsService();

// ============================================================
// 配置
// ============================================================
string syncRoot = builder.Configuration.GetValue<string>("SyncRoot")
               ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "CloudPan");

// Serilog 结构化日志
string logDir = Path.Combine(syncRoot, ".cloudpan", "logs");
try
{
    Directory.CreateDirectory(logDir);
}
catch (Exception ex)
{
    // Serilog 尚未初始化，必须用 MessageBox 告知用户（非交互模式写 Console.Error）
    ShowError("CloudPan — 启动失败",
        $"日志目录创建失败:\n{logDir}\n\n原因: {ex.Message}\n\n请检查同步根目录路径是否有效、磁盘是否可用。");
    Environment.Exit(1);
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



builder.WebHost.UseKestrel(options =>
{
    options.ListenAnyIP(SpecPorts.HttpPort); // HTTP (Phase 0)
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
builder.Services.AddSingleton<ISyncLogService, SyncLogService>();
builder.Services.AddSingleton<IWebSocketHandler, WebSocketHandler>();

// Controller
builder.Services.AddMemoryCache();
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
try
{
    storage.EnsureSyncRootExists();
}
catch (Exception ex)
{
    Console.Error.WriteLine($"同步根目录创建失败: {ex.Message}");
    Log.Fatal(ex, "同步根目录创建失败: {Path}", syncRoot);
    ShowError("CloudPan Server — 启动失败",
        $"同步根目录创建失败:\n{syncRoot}\n\n原因: {ex.Message}\n\n请检查路径是否有效、磁盘是否可用。");
    Environment.Exit(1);
}

using (var scope = app.Services.CreateScope())
{
    var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<CloudPanDbContext>>();
    await using var db = await dbFactory.CreateDbContextAsync();

    // 确保数据库已创建
    // 注: 当前使用 EnsureCreated()。后续版本考虑迁移至 EF Core Migrations
    await db.Database.EnsureCreatedAsync();

    // EnsureCreated 只在 DB 文件不存在时建表。如果 DB 已存在但是旧版本创建的
    // （缺少后续新增的表），需要手动补建。这是一个轻量的"schema 兼容层"。
    // 后续切换到 EF Core Migrations 后此段可删除。
    try
    {
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS Devices (
                Id      TEXT NOT NULL PRIMARY KEY,
                Name    TEXT NOT NULL DEFAULT '',
                Person  TEXT,
                LastSeen TEXT NOT NULL DEFAULT '',
                Online  INTEGER NOT NULL DEFAULT 0,
                RegisteredAt TEXT NOT NULL DEFAULT ''
            );
            """);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE INDEX IF NOT EXISTS idx_devices_lastseen ON Devices (LastSeen);
            """);
    }
    catch (Exception ex)
    {
        Log.Warning(ex, "schema 兼容补建失败（非致命，后续 EnsureCreated 可恢复）");
    }

    // 启动时 DB 完整性检查
    try
    {
        var integrityRows = await db.Database.SqlQueryRaw<string>("PRAGMA integrity_check;").ToListAsync();
        bool ok = integrityRows.Count == 1 && integrityRows[0] == "ok";
        if (!ok)
        {
            string msg = "DB 完整性检查失败: " + string.Join("; ", integrityRows);
            Log.Fatal(msg);
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine(msg);
            Console.WriteLine("请尝试还原备份或删除数据库文件后重新启动。");
            Console.ResetColor();
            Log.CloseAndFlush();
            Environment.Exit(1);
        }
        else
        {
            Log.Information("DB 完整性检查: 通过");
            Console.WriteLine("DB 完整性检查: 通过");
        }
    }
    catch (Exception ex)
    {
        string msg = "DB 完整性检查失败(异常): " + ex.Message;
        Log.Fatal(ex, msg);
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine(msg);
        Console.WriteLine("请检查数据库文件是否损坏或被占用。");
        Console.ResetColor();
        Log.CloseAndFlush();
        Environment.Exit(1);
    }


    // 设置 WAL 模式 + 启用外键约束
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
        string? presetToken = app.Configuration.GetValue<string>("CloudPan:Token");
        string tokenFile = Path.Combine(syncRoot, ".cloudpan", "token.txt");
        string token;
        if (!string.IsNullOrEmpty(presetToken))
        {
            token = presetToken;
            ServerTrayApp.Token = token;
        }
        else
        {
            // 生产环境：自动生成 64 字符随机 Token
            token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            ServerTrayApp.Token = token; // 供托盘菜单显示/复制

            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine();
            Console.WriteLine("╔══════════════════════════════════════════════════════════════╗");
            Console.WriteLine("║  家庭共享 Token（仅显示一次，请妥善保存）                    ║");
            Console.WriteLine($"║  {token}  ║");
            Console.WriteLine("╠══════════════════════════════════════════════════════════════╣");
            Console.WriteLine($"║  备份文件: {tokenFile,-47} ║");
            Console.WriteLine("║  （安全提示：配置完客户端后请删除此文件）                      ║");
            Console.WriteLine("╚══════════════════════════════════════════════════════════════╝");
            Console.WriteLine();
            Console.ResetColor();
        }
        // 持久化 Token 到文件（预设 Token 也写入，供安装向导读取）
        try
        {
            SecretStore.WriteToken(token, syncRoot);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Token 写入文件失败: {Path}。请手动创建该文件并写入 Token。", tokenFile);
        }
        string tokenHash = Convert.ToHexString(SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
        db.AppConfigs.Add(new AppConfig { Key = "token_hash", Value = tokenHash });
    }

    await db.SaveChangesAsync();

    // 启动时重置所有设备为离线状态（运行时会通过 WebSocket 重新标记在线）
    // 先检查 Devices 表是否存在，避免旧版无此表的 DB 启动时打印堆栈
    try
    {
        var tableExists = await db.Database.SqlQueryRaw<int>(
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='Devices';").ToListAsync();
        if (tableExists.Count > 0 && tableExists[0] > 0)
        {
            await db.Database.ExecuteSqlRawAsync("UPDATE Devices SET Online = 0");
        }
        else
        {
            Log.Information("Devices 表尚未创建，跳过设备在线状态重置");
        }
    }
    catch (Exception ex)
    {
        Log.Warning(ex, "重置设备在线状态失败（非致命）");
    }

    // WAL checkpoint（PASSIVE 模式：尝试将 WAL 写入主 DB，失败不截断 WAL，保留数据完整性）
    try { await db.Database.ExecuteSqlRawAsync("PRAGMA wal_checkpoint(PASSIVE);"); }
    catch (Exception ex) { Log.Warning(ex, "WAL checkpoint 失败（非致命）"); }

    // 释放 DbContext（因 schema 迁移可能替换实例，不能用 using 声明）
    if (db != null)
    {
        await db.DisposeAsync();
    }
}

// ============================================================
// 中间件管道
// ============================================================

// 中间件管道
// UseRouting 需在管道最前面——后续中间件通过 context.GetEndpoint() 读取端点元数据（EndpointAuthAttribute）
app.UseRouting();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });
app.UseRequestId();
app.UseGlobalExceptionHandler();
app.UseRateLimit();
app.UseTokenAuth();

// WebSocket /ws 路由（放在认证中间件之后，确保请求经过认证）
app.Map("/ws", async (HttpContext context, IWebSocketHandler handler) =>
{
    if (context.WebSockets.IsWebSocketRequest)
    {
        var socket = await context.WebSockets.AcceptWebSocketAsync();
        await handler.HandleConnectionAsync(socket, context);
    }
    else
    {
        context.Response.StatusCode = 400;
    }
});

app.MapControllers();

// 回收站 30 天自动清理
System.Threading.Timer trashCleanupTimer = new System.Threading.Timer(_ =>
{
    try
    {
        string trashDir = Path.Combine(syncRoot, ".cloudpan", ".trash");
        if (Directory.Exists(trashDir))
        {
            var cutoff = DateTime.UtcNow.AddDays(-30);
            foreach (string metaFile in Directory.GetFiles(trashDir, "*.json"))
            {
                try
                {
                    string json = System.IO.File.ReadAllText(metaFile);
                    using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(json);
                    var root = doc.RootElement;
                    string? deletedAt = root.TryGetProperty("DeletedAt", out var da) ? da.GetString() : null;
                    string? trashFileName = root.TryGetProperty("TrashFileName", out var tn) ? tn.GetString() : null;
                    if (deletedAt != null && trashFileName != null
                        && DateTime.TryParse(deletedAt, out var delTime) && delTime < cutoff)
                    {
                        string trashFile = Path.Combine(trashDir, trashFileName);
                        if (System.IO.File.Exists(trashFile))
                        {
                            System.IO.File.Delete(trashFile);
                        }

                        if (Directory.Exists(trashFile))
                        {
                            Directory.Delete(trashFile, recursive: true);
                        }

                        System.IO.File.Delete(metaFile);
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "清理回收站文件异常: {MetaFile}", metaFile);
                }
            }
        }
    }
    catch (Exception ex)
    {
        Log.Warning(ex, "回收站定时清理异常");
    }
}, null, TimeSpan.FromMinutes(10), TimeSpan.FromHours(6));

// ChunkedUpload 超时清理：每 30 分钟清理超过 24h 未完成的分块上传
System.Threading.Timer chunkCleanupTimer = new System.Threading.Timer(_ =>
{
    try
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var factory = app.Services.GetRequiredService<IDbContextFactory<CloudPanDbContext>>();
                var storage = app.Services.GetRequiredService<IFileStorageService>();
                await using var db = await factory.CreateDbContextAsync();
                string expiry = DateTime.UtcNow.AddHours(-24).ToString("O");
                var stale = await db.ChunkedUploads
                    .Where(c => string.Compare(c.CreatedAt, expiry) < 0)
                    .ToListAsync();
                foreach (var s in stale)
                {
                    try { if (File.Exists(s.TempPath)) { File.Delete(s.TempPath); } } catch (Exception ex) { Log.Warning(ex, "删除超时分块临时文件失败: {TempPath}", s.TempPath); }
                    db.ChunkedUploads.Remove(s);
                }
                if (stale.Count > 0)
                {
                    await db.SaveChangesAsync();
                    var logger = app.Services.GetRequiredService<ILogger<Program>>();
                    logger.LogInformation("清理超时分块上传: {Count} 条", stale.Count);
                }
            }
            catch (Exception ex) { Log.Warning(ex, "分块上传定时清理异常"); }
        });
    }
    catch (Exception ex) { Log.Warning(ex, "分块上传定时清理调度异常"); }
}, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30));

// WAL checkpoint：每 60 分钟执行一次 PRAGMA wal_checkpoint(TRUNCATE)，防止 WAL 文件无限增长
System.Threading.Timer walCheckpointTimer = new System.Threading.Timer(_ =>
{
    try
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var factory = app.Services.GetRequiredService<IDbContextFactory<CloudPanDbContext>>();
                await using var db = await factory.CreateDbContextAsync();
                await db.Database.ExecuteSqlRawAsync("PRAGMA wal_checkpoint(TRUNCATE);");
            }
            catch (Exception ex) { Log.Warning(ex, "WAL checkpoint 异常"); }
        });
    }
    catch (Exception ex) { Log.Warning(ex, "WAL checkpoint 调度异常"); }
}, null, TimeSpan.FromMinutes(60), TimeSpan.FromMinutes(60));

// 内存监控：每 10 分钟检查，超 500MB 告警
System.Threading.Timer memMonitor = new System.Threading.Timer(_ =>
{
    try
    {
        long memMb = GC.GetTotalMemory(false) / 1_048_576L;
        long ws = Environment.WorkingSet / 1_048_576L;
        if (ws > 500)
        {
            var logger = app.Services.GetRequiredService<ILogger<Program>>();
            logger.LogWarning("内存使用偏高: GC={GcMem}MB, WorkingSet={WsMem}MB", memMb, ws);
        }
    }
    catch (Exception ex)
    {
        Log.Warning(ex, "内存监控定时检查异常");
    }
}, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(10));

// 启动信息
app.Lifetime.ApplicationStarted.Register(() =>
{
    var logger = app.Services.GetRequiredService<ILogger<Program>>();
    logger.LogInformation("CloudPan Server v1.0.0 启动");

    // 控制台打印配置信息
    string serverUrl = "http://" + Environment.MachineName + ":" + SpecPorts.HttpPort;
    // 尝试获取本机 IP
    string localIP = "";
    try
    {
        var host = System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName());
        localIP = host.AddressList.FirstOrDefault(a =>
            a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)?.ToString() ?? "";
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "获取本机 IP 地址失败");
    }

    Console.WriteLine();
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine("╔══════════════════════════════════════════════════════╗");
    Console.WriteLine("║          CloudPan Server v1.0.0 已启动               ║");
    Console.WriteLine("╠══════════════════════════════════════════════════════╣");
    Console.WriteLine($"║  本机地址:  http://{localIP}:{SpecPorts.HttpPort}".PadRight(55) + "║");
    Console.WriteLine($"║  同步目录:  {syncRoot}".PadRight(55) + "║");
    Console.WriteLine("╚══════════════════════════════════════════════════════╝");
    Console.ResetColor();
});

app.Lifetime.ApplicationStopped.Register(() =>
{
    trashCleanupTimer.Dispose();
    chunkCleanupTimer.Dispose();
    walCheckpointTimer.Dispose();
    memMonitor.Dispose();
    Log.CloseAndFlush();
});

// 判断运行模式：Windows Service / console / tray GUI
bool useTray = args.Contains("--tray");
bool isService = Environment.UserInteractive == false || args.Contains("--service");

// UDP 局域网发现服务（客户端广播 "CLOUDPAN_DISCOVER" → 服务端回复连接信息）
CancellationTokenSource udpCts = new CancellationTokenSource();
_ = Task.Run(() => RunDiscoveryServiceAsync(syncRoot, udpCts.Token));
app.Lifetime.ApplicationStopping.Register(() => { try { udpCts.Cancel(); } catch { } });

if (useTray || !isService)
{
    ApplicationConfiguration.Initialize();

    // 检测是否已安装为服务，未安装则显示安装向导
    bool serviceInstalled = IsServiceInstalled("CloudPanServer");
    if (!serviceInstalled)
    {
        using WindowsIdentity identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        WindowsPrincipal principal = new System.Security.Principal.WindowsPrincipal(identity);
        bool isAdmin = principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);

        if (isAdmin)
        {
            // 管理员：建议安装为 Windows Service
            var result = MessageBox.Show(
                "CloudPan 服务尚未安装为 Windows Service。\n\n" +
                "点击「是」打开安装向导（推荐，开机自启）。\n" +
                "点击「否」以独立模式运行（本次会话有效）。",
                "CloudPan Server — 首次运行",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (result == DialogResult.Yes)
            {
                ServerInstaller installer = new ServerInstaller();
                var installResult = installer.ShowDialog();
                if (installResult == DialogResult.OK)
                {
                    // 安装成功 → 退出当前进程，避免与服务端口冲突
                    Log.Information("服务安装成功，当前进程退出。服务已在后台运行。");
                    Log.CloseAndFlush();
                    Environment.Exit(0);
                }
                else if (installResult == DialogResult.Abort)
                {
                    // 安装向导因权限等问题退出
                    Log.Warning("安装向导异常退出，以独立模式运行");
                    Log.Information("提示：本窗口关闭后服务将停止。建议以管理员身份运行并安装为 Windows Service。");
                }
            }
            else
            {
                // 管理员选择"否"——以独立模式运行
                Log.Information("以独立模式运行（关闭本窗口后服务停止）");
                Console.WriteLine("以独立模式运行 —— 关闭本窗口后服务停止。");
                Console.WriteLine("如需开机自启，请重新运行并选择「是」安装为 Windows Service。");
            }
        }
        else
        {
            // 非管理员：直接以独立模式运行，不弹安装向导
            Log.Information("非管理员，以独立模式运行（关闭本窗口后服务停止）");
            Console.WriteLine("以独立模式运行 —— 关闭本窗口后服务停止。");
            Console.WriteLine("如需安装为 Windows Service，请以管理员身份运行此程序。");
        }
    }

    // 必须在 RunAsync 之前创建 UI（之后 DI 容器会释放）
    var dbFactory = app.Services.GetRequiredService<IDbContextFactory<CloudPanDbContext>>();
    ServerWindow window = new ServerWindow(dbFactory);
    ServerTrayApp tray = new ServerTrayApp(app, window);
    window.AddLog("Web 服务启动中...");
    var serverTask = app.RunAsync();
    _ = serverTask.ContinueWith(t =>
    {
        if (t.IsFaulted)
        {
            var ex = t.Exception!.GetBaseException();
            Log.Fatal(ex, "Web 服务异常退出");
            // 区分端口冲突与其他错误
            if (ex.Message.Contains("address already in use", StringComparison.OrdinalIgnoreCase)
                || ex.Message.Contains("Access denied", StringComparison.OrdinalIgnoreCase))
            {
                window.AddLog($"端口 {SpecPorts.HttpPort} 被占用，请检查是否有其他 CloudPan 实例或程序正在使用该端口。");
            }
            else
            {
                window.AddLog($"Web 服务异常退出: {ex.Message}");
            }
            Environment.ExitCode = 1;
        }
        else if (t.IsCompletedSuccessfully)
        {
            window.AddLog("Web 服务已正常停止");
        }
    }, TaskContinuationOptions.NotOnCanceled);

    // 等待 Web 服务器启动就绪（最多 5 秒）
    try
    {
        Task readyDelay = Task.Delay(5000);
        var completed = await Task.WhenAny(serverTask, readyDelay);
        if (completed == serverTask && serverTask.IsFaulted)
        {
            // 启动失败，ContinueWith 已处理
            window.AddLog("Web 服务启动失败");
        }
        else
        {
            window.AddLog("Web 服务已启动");
        }
    }
    catch (Exception ex)
    {
        Log.Fatal(ex, "Web 服务启动失败");
        window.AddLog($"Web 服务启动失败: {ex.Message}");
    }

    Application.Run(tray);

    // 托盘退出后：等待 Web 服务器停止 + 刷新日志
    if (!serverTask.IsCompleted)
    {
        try
        {
            await app.StopAsync();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "停止 Web 服务时异常");
        }
    }
    Log.CloseAndFlush();
}
else
{
    try
    {
        app.Run();
    }
    catch (Exception ex)
    {
        Log.Fatal(ex, "Web 服务运行异常");
        Environment.ExitCode = 1;
    }
    Log.CloseAndFlush();
}
return;

/// <summary>检查 Windows 服务是否已安装。</summary>
static bool IsServiceInstalled(string serviceName)
{
    try
    {
        using var sc = ServiceController.GetServices()
            .FirstOrDefault(s => s.ServiceName == serviceName);
        return sc != null;
    }
    catch (Exception ex)
    {
        Log.Warning(ex, "检查服务 {ServiceName} 安装状态时发生异常（可能是权限不足）", serviceName);
        return false;
    }
}

/// <summary>监听 UDP 发现端口（见 SpecPorts.UdpDiscoveryPort），响应局域网客户端发现请求。</summary>
static async Task RunDiscoveryServiceAsync(string syncRoot, CancellationToken ct)
{
    try
    {
        using UdpClient udp = new UdpClient(new IPEndPoint(IPAddress.Any, SpecPorts.UdpDiscoveryPort));
        byte[] serverInfo = Encoding.UTF8.GetBytes(
            "{\"server\":\"http://" + Environment.MachineName + ":" + SpecPorts.HttpPort + "\",\"name\":\"" +
            Environment.MachineName + "\",\"version\":\"0.2.0\"}");
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await udp.ReceiveAsync(ct);
                string msg = Encoding.UTF8.GetString(result.Buffer).Trim();
                if (msg == "CLOUDPAN_DISCOVER")
                {
                    await udp.SendAsync(serverInfo, serverInfo.Length, result.RemoteEndPoint);
                }
            }
            catch (OperationCanceledException) { break; }
        }
    }
    catch (Exception ex)
    {
        Log.Warning(ex, "UDP 发现服务异常");
    }
}


/// <summary>
/// 显示错误对话框（仅在交互模式下），同时写入 Console.Error。
/// 在非交互模式（Windows Service / 测试环境）下省略对话框以避免崩溃。
/// </summary>
static void ShowError(string title, string message)
{
    Console.Error.WriteLine($"[{title}] {message}");
    if (Environment.UserInteractive)
    {
        try { MessageBox.Show(message, title, MessageBoxButtons.OK, MessageBoxIcon.Error); }
        catch { /* 非交互环境忽略 */ }
    }
}

/// <summary>使测试项目可见。</summary>
public partial class Program { }
