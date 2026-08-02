using CloudPan.Client.Models;
using CloudPan.Client.Services;
using CloudPan.Client.UI;
using CloudPan.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;

namespace CloudPan.Client;

/// <summary>客户端入口：托盘常驻 + 管理窗口。</summary>
public static class Program
{
    public static string SyncRoot { get; private set; } = "";
    public static string ServerUrl { get; private set; } = $"http://localhost:{SpecPorts.HttpPort}";
    public static string Token { get; private set; } = "";
    /// <summary>启动时连接检测结果（true = 离线），运行中由 WebSocket 事件更新。</summary>
    public static bool IsOffline { get; internal set; }

    [STAThread]
    // 同步 Main：所有初始化在 STA UI 线程执行，避免 async 延续在线程池跨线程操作 UI 控件（initForm.Close 死锁）
    public static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        // 0. 全局异常处理（防止未处理异常静默崩溃进程）
        Application.ThreadException += OnThreadException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        // 1. 解析命令行参数 / 读取保存的配置
        //   CloudPan.Client.exe [serverUrl] [syncRoot] [token]
        if (args.Length >= 1)
        {
            ServerUrl = args[0];
        }

        if (args.Length >= 2)
        {
            SyncRoot = args[1];
        }

        if (args.Length >= 3)
        {
            Token = args[2];
        }

        // 加载配置（JSON 格式，支持旧版 config.txt 自动迁移）
        string savedConfig = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CloudPan", "client-config.json");
        ClientConfig clientConfig = ClientConfig.Load(savedConfig);

        // 命令行参数优先于保存的配置；仅当存在已保存配置时才用其覆盖 localhost/默认占位值
        //（避免无配置时把传入的 localhost 地址/同步根清空而误弹配置窗口）
        if (string.IsNullOrEmpty(ServerUrl)
            || (ServerUrl.StartsWith("http://localhost") && !string.IsNullOrEmpty(clientConfig.ServerUrl)))
        {
            ServerUrl = clientConfig.ServerUrl;
        }

        if (string.IsNullOrEmpty(SyncRoot)
            || (SyncRoot.StartsWith(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "CloudPan"))
                && !string.IsNullOrEmpty(clientConfig.SyncRoot)))
        {
            SyncRoot = clientConfig.SyncRoot;
        }

        if (string.IsNullOrEmpty(Token))
        {
            try
            {
                byte[] encrypted = Convert.FromBase64String(clientConfig.TokenEncrypted);
                Token = System.Text.Encoding.UTF8.GetString(
                    System.Security.Cryptography.ProtectedData.Unprotect(encrypted, null,
                        System.Security.Cryptography.DataProtectionScope.CurrentUser));
            }
            catch (Exception ex)
            {
                // DPAPI 解密失败：不降级到明文，标记为需要重新输入
                // 此时 Serilog 尚未初始化，使用 MessageBox 通知用户
                MessageBox.Show(
                    $"Token 解密失败（DPAPI），需重新配置连接。\n\n原因: {ex.Message}",
                    "CloudPan — Token 解密失败",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                Token = ""; // 触发 SetupForm 让用户重新输入 Token
            }
        }

        // 首次运行：显示配置窗口
        if (string.IsNullOrEmpty(Token) || string.IsNullOrEmpty(ServerUrl) || string.IsNullOrEmpty(SyncRoot))
        {
            SyncRoot = string.IsNullOrEmpty(SyncRoot)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "CloudPan")
                : SyncRoot;

            SetupForm setupForm = new SetupForm(ServerUrl, SyncRoot, Token);
            if (setupForm.ShowDialog() != DialogResult.OK)
            {
                return;
            }

            ServerUrl = setupForm.ServerUrl;
            SyncRoot = setupForm.SyncRoot;
            Token = setupForm.Token;

            // 保存到结构化 JSON（Token 使用 DPAPI 加密），失败时允许重试
            while (true)
            {
                try
                {
                    byte[] tokenBytes = System.Security.Cryptography.ProtectedData.Protect(
                        System.Text.Encoding.UTF8.GetBytes(Token), null,
                        System.Security.Cryptography.DataProtectionScope.CurrentUser);
                    ClientConfig savedCfg = new ClientConfig
                    {
                        ServerUrl = ServerUrl,
                        SyncRoot = SyncRoot,
                        TokenEncrypted = Convert.ToBase64String(tokenBytes),
                    };
                    savedCfg.Save(savedConfig);
                    break;
                }
                catch (Exception ex)
                {
                    var retry = MessageBox.Show(
                        $"配置保存失败:\n{ex.Message}\n\n请检查磁盘空间和配置文件路径的写入权限。",
                        "CloudPan — 配置保存失败",
                        MessageBoxButtons.RetryCancel, MessageBoxIcon.Error);
                    if (retry != DialogResult.Retry)
                    {
                        return;
                    }
                }
            }
        }

        // ── 初始化进度提示（防止配置完成后到托盘出现之前的黑屏/闪烁） ──
        using Form initForm = new Form
        {
            Text = "CloudPan",
            Size = new Size(320, 90),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            ControlBox = false,
            StartPosition = FormStartPosition.CenterScreen,
            ShowInTaskbar = false,
            MinimizeBox = false,
            MaximizeBox = false,
        };
        initForm.Controls.Add(new Label
        {
            Text = "正在初始化...",
            Dock = DockStyle.Top,
            TextAlign = ContentAlignment.MiddleCenter,
            Height = 30,
            Top = 15,
        });
        initForm.Controls.Add(new ProgressBar
        {
            Style = ProgressBarStyle.Marquee,
            Dock = DockStyle.Top,
            Height = 20,
            Top = 45,
        });
        initForm.Show();
        Application.DoEvents();

        try
        {
            Directory.CreateDirectory(SyncRoot);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"同步目录创建失败:\n{SyncRoot}\n\n原因: {ex.Message}\n\n请检查路径是否有效、磁盘是否可用。",
                "CloudPan — 启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        string configDir = Path.Combine(SyncRoot, ".cloudpan");
        string dbPath = Path.Combine(configDir, "client.db");
        // 注入同步根到 SettingsStore（领域层，避免其捕获陈旧 Program.SyncRoot）
        SettingsStore.SetSyncRoot(SyncRoot);
        try
        {
            Directory.CreateDirectory(configDir);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"配置目录创建失败:\n{configDir}\n\n原因: {ex.Message}\n\n请检查磁盘空间和权限。",
                "CloudPan — 启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        // 设备 ID：首次启动生成 GUID，持久化到 .cloudpan/device.id
        string deviceIdPath = Path.Combine(configDir, "device.id");
        string? deviceId = null;
        try
        {
            if (File.Exists(deviceIdPath))
            {
                deviceId = File.ReadAllText(deviceIdPath).Trim();
            }
        }
        catch (Exception ex)
        {
            // Serilog 尚未初始化，使用 Console 输出（发布版静默）
            System.Diagnostics.Debug.WriteLine($"读取设备 ID 失败: {ex.Message}");
        }
        if (string.IsNullOrEmpty(deviceId))
        {
            deviceId = Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(deviceIdPath, deviceId);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"写入设备 ID 失败: {ex.Message}");
            }
        }

        // 2. Serilog 日志初始化
        string logDir = Path.Combine(SyncRoot, ".cloudpan", "logs");
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
        ServiceCollection services = new ServiceCollection();

        services.AddLogging(b => b.AddSerilog(dispose: true));

        // 配置
        // 重新加载配置（如果 SetupForm 刚保存过）
        ClientConfig cfg = ClientConfig.Load(savedConfig);
        SyncConfig syncConfig = new SyncConfig
        {
            SyncRoot = SyncRoot,
            ServerUrl = ServerUrl,
            Token = Token,
            DeviceId = deviceId,
            UploadSpeedLimitBps = cfg.UploadLimitBps,
            DownloadSpeedLimitBps = cfg.DownloadLimitBps,
            SelectedPaths = cfg.SelectedPaths,
        };
        services.AddSingleton(syncConfig);

        // 数据库（DbContextFactory 确保并发安全）
        services.AddSingleton<IDbContextFactory<ClientDbContext>>(_ => new ClientDbFactory(dbPath));
        EnsureDbCreated(dbPath);

        // 数据库完整性检查（PRAGMA quick_check）
        try
        {
            using ClientDbContext checkDb = new ClientDbContext(dbPath);
            // SqlQueryRaw<string> 用于执行 PRAGMA 并读取返回标量；若 EF Core 提供
            // 商不支持则静默跳过（见 catch 块）。
            string? result = checkDb.Database.SqlQueryRaw<string>("PRAGMA quick_check;").AsEnumerable().FirstOrDefault();
            if (result != "ok")
            {
                Log.Warning("数据库完整性检查失败: quick_check 返回 '{Result}'", result ?? "null");
                var rebuild = MessageBox.Show(
                    "同步数据库已损坏，是否重建？\n\n重建将清空传输队列和同步状态，不影响已同步的文件。",
                    "CloudPan — 数据库损坏",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (rebuild == DialogResult.Yes)
                {
                    // 备份旧数据库
                    try
                    {
                        string backupPath = dbPath + $".bak.{DateTime.Now:yyyyMMddHHmmss}";
                        File.Copy(dbPath, backupPath);
                        Log.Information("已备份损坏的数据库到 {BackupPath}", backupPath);
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "备份损坏数据库失败，继续重建");
                    }
                    // 删除并重建
                    checkDb.Database.EnsureDeleted();
                    // 注: 当前使用 EnsureCreated()。后续版本考虑迁移至 EF Core Migrations 以获得增量迁移能力
                    checkDb.Database.EnsureCreated();
                    checkDb.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
                    Log.Information("数据库已重建");
                }
                else
                {
                    Log.Information("用户选择退出程序");
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "数据库完整性检查异常（PRAGMA 可能不受当前提供程序支持），跳过检查");
        }

        // HTTP 客户端（Phase 0：自签证书静默接受）
        services.AddSingleton<IApiClient>(new ApiClient(ServerUrl, Token, deviceId,
            syncConfig.UploadSpeedLimitBps, syncConfig.DownloadSpeedLimitBps));

        // 服务层（显式工厂构造，避免 SyncEngine ⇄ FileWatcherService 的 DI 循环依赖）
        services.AddSingleton<WebSocketClient>(sp =>
        {
            var cfg = sp.GetRequiredService<SyncConfig>();
            return new WebSocketClient(cfg, sp.GetRequiredService<ILoggerFactory>().CreateLogger<WebSocketClient>());
        });
        services.AddSingleton<SyncEngine>(sp =>
        {
            var cfg = sp.GetRequiredService<SyncConfig>();
            var api = sp.GetRequiredService<IApiClient>();
            var dbFactory = sp.GetRequiredService<IDbContextFactory<ClientDbContext>>();
            var ws = sp.GetRequiredService<WebSocketClient>();
            return new SyncEngine(api, cfg, dbFactory,
                sp.GetRequiredService<ILoggerFactory>().CreateLogger<SyncEngine>(), ws, null);
        });
        services.AddSingleton<FileWatcherService>(sp =>
        {
            var cfg = sp.GetRequiredService<SyncConfig>();
            var engine = sp.GetRequiredService<SyncEngine>();
            return new FileWatcherService(cfg, engine,
                sp.GetRequiredService<ILoggerFactory>().CreateLogger<FileWatcherService>());
        });

        var provider = services.BuildServiceProvider();

        // 4. 健康检查（同步执行，不阻塞启动即可）
        var apiClient = provider.GetRequiredService<IApiClient>();
        var loggerFactory = provider.GetRequiredService<ILoggerFactory>();
        var logger = loggerFactory.CreateLogger("CloudPan.Client");

        // 连接检测（5 秒超时，使用 CancellationToken 取消 HTTP 请求）
        initForm.Controls.OfType<Label>().First().Text = "正在连接服务端...";
        Application.DoEvents();

        using CancellationTokenSource healthCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        bool connected = apiClient.HealthCheckAsync(healthCts.Token).GetAwaiter().GetResult();

        if (!connected)
        {
            IsOffline = true;
            logger.LogWarning("无法连接到服务端 {ServerUrl}（5秒超时）", ServerUrl);
            logger.LogInformation("客户端以离线模式运行，连接恢复后自动同步，托盘图标将显示离线状态");

            // 询问用户是否重新配置
            var reconfigure = MessageBox.Show(
                $"无法连接到服务端:\n{ServerUrl}\n\n" +
                "是否重新配置连接地址？\n\n" +
                "选择「是」重新配置，选择「否」以离线模式运行。",
                "CloudPan — 连接失败",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (reconfigure == DialogResult.Yes)
            {
                // 重新弹出配置窗口
                SetupForm setupForm = new SetupForm(ServerUrl, SyncRoot, Token);
                if (setupForm.ShowDialog() == DialogResult.OK)
                {
                    ServerUrl = setupForm.ServerUrl;
                    SyncRoot = setupForm.SyncRoot;
                    Token = setupForm.Token;

                    // 保存新配置，然后重启客户端让新配置在 DI 初始化阶段生效
                    try
                    {
                        byte[] tokenBytes = System.Security.Cryptography.ProtectedData.Protect(
                            System.Text.Encoding.UTF8.GetBytes(Token), null,
                            System.Security.Cryptography.DataProtectionScope.CurrentUser);
                        ClientConfig newCfg = new ClientConfig
                        {
                            ServerUrl = ServerUrl,
                            SyncRoot = SyncRoot,
                            TokenEncrypted = Convert.ToBase64String(tokenBytes),
                        };
                        newCfg.Save(savedConfig);
                        logger.LogInformation("配置已更新，重启客户端以应用新配置");
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "保存更新后的配置失败");
                        MessageBox.Show(
                            $"保存更新后的配置失败:\n{ex.Message}\n\n请检查磁盘空间和写入权限。",
                            "CloudPan — 保存失败",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
                // 无论保存成功或失败（或用户取消），都退出让用户重新启动
                logger.LogInformation("用户重新配置后退出，请重新启动客户端");
                return;
            }
        }
        else
        {
            IsOffline = false;
            logger.LogInformation("已连接到服务端 {ServerUrl}", ServerUrl);
            // 检查版本更新
            var clientVer = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version;
            string clientVerStr = clientVer != null
                ? $"{clientVer.Major}.{clientVer.Minor}.{clientVer.Build}"
                : "1.0.0";
            try
            {
                using CancellationTokenSource versionCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                using HttpClient http = new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = new Uri(ServerUrl.TrimEnd('/')) };
                http.Timeout = TimeSpan.FromSeconds(5);
                var versionResp = http.GetAsync("/api/version", versionCts.Token).GetAwaiter().GetResult();
                if (versionResp.IsSuccessStatusCode)
                {
                    string versionJson = versionResp.Content.ReadAsStringAsync(versionCts.Token).GetAwaiter().GetResult();
                    using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(versionJson);
                    string? serverVersion = doc.RootElement.GetProperty("version").GetString();
                    if (serverVersion != null && serverVersion != clientVerStr)
                    {
                        logger.LogInformation("服务端版本 {ServerVer}，当前 {ClientVer}", serverVersion, clientVerStr);
                    }
                }
            }
            catch { /* 版本检查失败不影响正常使用 */ }
        }

        // 5. 启动文件监控
        var watcher = provider.GetRequiredService<FileWatcherService>();
        watcher.Start();

        // 6. 启动同步引擎 + WebSocket + 托盘应用
        var engine = provider.GetRequiredService<SyncEngine>();
        var wsClient = provider.GetRequiredService<WebSocketClient>();

        // 运行时连接状态追踪（同步 IsOffline 标志，供托盘和 UI 读取）
        wsClient.OnConnected += OnWsConnected;
        wsClient.OnDisconnected += OnWsDisconnected;

        // 关闭初始化进度提示，进入托盘常驻
        initForm.Close();

        Application.Run(new TrayAppContext(engine, wsClient));
    }

    // ===== 全局异常处理器（具名方法，CP301：可退订） =====

    /// <summary>UI 线程未处理异常：记录崩溃日志并退出。</summary>
    private static void OnThreadException(object sender, System.Threading.ThreadExceptionEventArgs e)
    {
        try { File.AppendAllText(GetCrashLogPath(), $"[UI线程异常] {DateTime.UtcNow:O}\n{e.Exception}\n\n"); }
        catch { /* 最后一道防线——写文件也失败则放弃 */ }
        MessageBox.Show($"CloudPan 遇到未处理的错误，即将退出。\n\n{e.Exception.Message}",
            "CloudPan — 错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        Environment.Exit(1);
    }

    /// <summary>AppDomain 未处理异常：记录崩溃日志。</summary>
    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        try { File.AppendAllText(GetCrashLogPath(), $"[未处理异常] {DateTime.UtcNow:O}\n{e.ExceptionObject}\n\n"); }
        catch { }
    }

    /// <summary>未观察 Task 异常：记录并标记已观察，防止进程崩溃。</summary>
    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        try { File.AppendAllText(GetCrashLogPath(), $"[未观察Task异常] {DateTime.UtcNow:O}\n{e.Exception}\n\n"); }
        catch { }
        e.SetObserved(); // 防止进程崩溃
    }

    /// <summary>WebSocket 连接建立：清除离线标志。</summary>
    private static void OnWsConnected() => IsOffline = false;

    /// <summary>WebSocket 断开：设置离线标志。</summary>
    private static void OnWsDisconnected() => IsOffline = true;

    /// <summary>崩溃日志路径（%LocalAppData%\CloudPan\crash.log），用于全局异常处理器记录。</summary>
    private static string GetCrashLogPath()
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CloudPan");
        try { Directory.CreateDirectory(dir); } catch { }
        return Path.Combine(dir, "crash.log");
    }

    private static void EnsureDbCreated(string dbPath)
    {
        using ClientDbContext db = new ClientDbContext(dbPath);
        // 注: 当前使用 EnsureCreated()。后续版本考虑迁移至 EF Core Migrations 以获得增量迁移能力
        db.Database.EnsureCreated();
        db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
    }
}

/// <summary>简易 DbContextFactory 实现。</summary>
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
