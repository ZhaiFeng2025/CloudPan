using System.Security.Cryptography;
using System.Text;
using CloudPan.Client.Core.Models;
using CloudPan.Client.Core.Services;
using CloudPan.Contract;
using CloudPan.Infrastructure.Persistence;
using CloudPan.Infrastructure.Persistence.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;

namespace CloudPan.Client.Core.Composition;

/// <summary>
/// 客户端启动期解析结果：CLI 参数与已保存配置合并后的生效值。
/// </summary>
public sealed class ClientStartupResult
{
    /// <summary>服务端地址。</summary>
    public string ServerUrl { get; }

    /// <summary>本地同步根目录绝对路径。</summary>
    public string SyncRoot { get; }

    /// <summary>家庭共享 Token（DPAPI 解密后，失败则为空）。</summary>
    public string Token { get; }

    /// <summary>Token DPAPI 解密失败原因（null = 正常）；非空时由 UI 提示用户重新配置。</summary>
    public string? TokenDecryptError { get; }

    /// <summary>启动时从磁盘加载的持久化配置（ResolveStartup 唯一读盘结果，供 BuildContainer 复用，T-043）。</summary>
    public ClientConfig Config { get; }

    internal ClientStartupResult(string serverUrl, string syncRoot, string token, string? tokenDecryptError, ClientConfig config)
    {
        ServerUrl = serverUrl;
        SyncRoot = syncRoot;
        Token = token;
        TokenDecryptError = tokenDecryptError;
        Config = config;
    }
}

/// <summary>客户端数据库完整性检查结果。</summary>
public enum DatabaseIntegrityStatus
{
    /// <summary>PRAGMA quick_check 通过（或不受支持跳过）。</summary>
    Ok,

    /// <summary>数据库损坏，需用户决定重建。</summary>
    Corrupt,
}

/// <summary>连接检测结果。</summary>
public sealed class ConnectionResult
{
    /// <summary>是否在 5 秒超时内连上服务端。</summary>
    public bool Connected { get; }

    internal ConnectionResult(bool connected)
    {
        Connected = connected;
    }
}

/// <summary>
/// 客户端组合根（启动编排）：配置解析/DPAPI/建库与完整性/DI 装配/连接检测。
/// 不引用 WinForms（R-A2），供 Client.UI Program.cs 复用，对齐服务端薄组合根先例（T-029 / F-29）。
/// </summary>
public sealed class ClientBootstrap
{
    /// <summary>默认配置文件路径（%LocalAppData%\CloudPan\client-config.json）。</summary>
    public static string GetConfigPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CloudPan", "client-config.json");

    /// <summary>
    /// 解析并合并配置：CLI 参数（[serverUrl] [syncRoot] [token]）优先，
    /// 未提供时回退已保存配置；Token 经 DPAPI 解密，失败时经 TokenDecryptError 上报。
    /// </summary>
    public static ClientStartupResult ResolveStartup(string[] args)
    {
        ClientConfig savedConfig = ClientConfig.Load(GetConfigPath());

        string serverUrl = args.Length >= 1 ? args[0] : "";
        string syncRoot = args.Length >= 2 ? args[1] : "";
        string token = args.Length >= 3 ? args[2] : "";

        // 命令行参数优先于保存的配置；仅当存在已保存配置时才用其覆盖 localhost/默认占位值
        //（避免无配置时把传入的 localhost 地址/同步根清空而误弹配置窗口）
        if (string.IsNullOrEmpty(serverUrl)
            || (serverUrl.StartsWith("http://localhost") && !string.IsNullOrEmpty(savedConfig.ServerUrl)))
        {
            serverUrl = savedConfig.ServerUrl;
        }

        string defaultSyncRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "CloudPan");
        if (string.IsNullOrEmpty(syncRoot)
            || (syncRoot.StartsWith(defaultSyncRoot) && !string.IsNullOrEmpty(savedConfig.SyncRoot)))
        {
            syncRoot = savedConfig.SyncRoot;
        }

        string? tokenDecryptError = null;
        if (string.IsNullOrEmpty(token))
        {
            try
            {
                byte[] encrypted = Convert.FromBase64String(savedConfig.TokenEncrypted);
                token = Encoding.UTF8.GetString(ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser));
            }
            catch (Exception ex)
            {
                // DPAPI 解密失败：不降级到明文，标记为需要重新输入
                tokenDecryptError = ex.Message;
                token = "";
            }
        }

        return new ClientStartupResult(serverUrl, syncRoot, token, tokenDecryptError, savedConfig);
    }

    /// <summary>DPAPI 加密 Token 并持久化配置（JSON，原子写入）。失败抛异常，由 UI 决定重试。</summary>
    public static void SaveConfig(string serverUrl, string syncRoot, string token)
    {
        byte[] tokenBytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(token), null, DataProtectionScope.CurrentUser);
        ClientConfig cfg = new ClientConfig
        {
            ServerUrl = serverUrl,
            SyncRoot = syncRoot,
            TokenEncrypted = Convert.ToBase64String(tokenBytes),
        };
        cfg.Save(GetConfigPath());
    }

    private readonly string _serverUrl;
    private readonly string _syncRoot;
    private readonly string _token;
    private readonly ClientConfig _config;

    /// <summary>配置目录（{syncRoot}/.cloudpan）。</summary>
    public string ConfigDir { get; private set; } = "";

    /// <summary>客户端数据库文件路径。</summary>
    public string DbPath { get; private set; } = "";

    /// <summary>设备 ID（首次启动生成并持久化到 .cloudpan/device.id）。</summary>
    public string DeviceId { get; private set; } = "";

    /// <summary>DI 容器（Prepare 之后可用）。</summary>
    public ServiceProvider Provider { get; private set; } = null!;

    /// <summary>创建组合根实例（服务端地址/同步根/Token 需为最终生效值；config 为 ResolveStartup 已读盘的持久化配置，复用避免二次读盘，T-043）。</summary>
    public ClientBootstrap(string serverUrl, string syncRoot, string token, ClientConfig config)
    {
        _serverUrl = serverUrl;
        _syncRoot = syncRoot;
        _token = token;
        _config = config;
    }

    /// <summary>
    /// 启动装配：建目录 → 设备 ID → Serilog → DI 容器 → 建库（Migrate）。
    /// 目录创建失败抛 InvalidOperationException（消息含 UI 提示文案）。
    /// </summary>
    public void Prepare()
    {
        try
        {
            Directory.CreateDirectory(_syncRoot);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"同步目录创建失败:\n{_syncRoot}\n\n原因: {ex.Message}\n\n请检查路径是否有效、磁盘是否可用。", ex);
        }

        ConfigDir = Path.Combine(_syncRoot, ".cloudpan");
        DbPath = Path.Combine(ConfigDir, "client.db");
        try
        {
            Directory.CreateDirectory(ConfigDir);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"配置目录创建失败:\n{ConfigDir}\n\n原因: {ex.Message}\n\n请检查磁盘空间和权限。", ex);
        }

        DeviceId = LoadOrCreateDeviceId(ConfigDir);
        InitializeLogging(_syncRoot);
        Provider = BuildContainer();
        EnsureDbCreated(DbPath);
    }

    /// <summary>数据库完整性检查（PRAGMA quick_check）。PRAGMA 异常视为通过（不受支持时跳过检查）。</summary>
    public DatabaseIntegrityStatus CheckDatabaseIntegrity()
    {
        try
        {
            // 仅经 IDbContextFactory 使用客户端 DbContext，不直接持有持久化实现（T-068）
            using ClientDbContext checkDb = Provider.GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContext();
            string? result = checkDb.Database.SqlQueryRaw<string>("PRAGMA quick_check;").AsEnumerable().FirstOrDefault();
            if (result != "ok")
            {
                Log.Warning("数据库完整性检查失败: quick_check 返回 '{Result}'", result ?? "null");
                return DatabaseIntegrityStatus.Corrupt;
            }
            return DatabaseIntegrityStatus.Ok;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "数据库完整性检查异常（PRAGMA 可能不受当前提供程序支持），跳过检查");
            return DatabaseIntegrityStatus.Ok;
        }
    }

    /// <summary>备份并重建损坏的数据库（重建走 Migrate 建全表并恢复 WAL 模式）。</summary>
    public void RebuildDatabase()
    {
        try
        {
            string backupPath = DbPath + $".bak.{DateTime.Now:yyyyMMddHHmmss}";
            File.Copy(DbPath, backupPath);
            Log.Information("已备份损坏的数据库到 {BackupPath}", backupPath);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "备份损坏数据库失败，继续重建");
        }

        using ClientDbContext db = Provider.GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContext();
        db.Database.EnsureDeleted();
        db.Database.Migrate();
        SqlitePragma.EnsureWAL(db);
        Log.Information("数据库已重建");
    }

    /// <summary>连接检测（5 秒超时）。</summary>
    public ConnectionResult HealthCheck()
    {
        var apiClient = Provider.GetRequiredService<IApiClient>();
        var logger = Provider.GetRequiredService<ILoggerFactory>().CreateLogger("CloudPan.Client");

        using CancellationTokenSource healthCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        bool connected = apiClient.HealthCheckAsync(healthCts.Token).GetAwaiter().GetResult();

        if (!connected)
        {
            logger.LogWarning("无法连接到服务端 {ServerUrl}（5秒超时）", _serverUrl);
            logger.LogInformation("客户端以离线模式运行，连接恢复后自动同步，托盘图标将显示离线状态");
            return new ConnectionResult(false);
        }

        logger.LogInformation("已连接到服务端 {ServerUrl}", _serverUrl);
        return new ConnectionResult(true);
    }

    // ===== 私有辅助 =====

    private static string LoadOrCreateDeviceId(string configDir)
    {
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
            // Serilog 尚未初始化，使用 Debug 输出（发布版静默）
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

        return deviceId;
    }

    private static void InitializeLogging(string syncRoot)
    {
        string logDir = Path.Combine(syncRoot, ".cloudpan", "logs");
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
    }

    private ServiceProvider BuildContainer()
    {
        ServiceCollection services = new ServiceCollection();

        services.AddLogging(b => b.AddSerilog(dispose: true));

        // 复用 ResolveStartup 已读盘的配置（T-043：读盘收敛为 1 次，BuildContainer 不再重复 Load）
        ClientConfig cfg = _config;
        // 持久化配置单例注册：供 UI（MainWindow 托盘关闭提示 / TrayAppContext 设置窗口）读写同一实例
        services.AddSingleton(cfg);
        SyncConfig syncConfig = new SyncConfig
        {
            SyncRoot = _syncRoot,
            ServerUrl = _serverUrl,
            Token = _token,
            DeviceId = DeviceId,
            UploadSpeedLimitBps = cfg.UploadLimitBps,
            DownloadSpeedLimitBps = cfg.DownloadLimitBps,
            SelectedPaths = cfg.SelectedPaths,
        };
        services.AddSingleton(syncConfig);

        // 数据库（DbContextFactory 确保并发安全）
        services.AddSingleton<IDbContextFactory<ClientDbContext>>(_ => new ClientDbFactory(DbPath));

        // HTTP 客户端（Phase 0：自签证书静默接受）
        services.AddSingleton<IApiClient>(new ApiClient(_serverUrl, _token, DeviceId,
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

        return services.BuildServiceProvider();
    }

    private void EnsureDbCreated(string dbPath)
    {
        // 仅经 IDbContextFactory 使用客户端 DbContext，不直接持有持久化实现（T-068）
        using ClientDbContext db = Provider.GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContext();
        // EF Migrations 建库/升级（初始迁移幂等：全新库建全表，旧库已有表跳过、缺失的表补建，T-008）
        db.Database.Migrate();
        // WAL 策略单一实现（T-068）
        SqlitePragma.EnsureWAL(db);
        // 旧库列级兼容：EnsureCreated 时代创建的 SyncQueue 缺 TargetPath 列（重命名操作字段），
        // 经 PRAGMA 判断后 ALTER 补列（SQLite ALTER ADD COLUMN 无 IF NOT EXISTS，先查后补保证幂等）
        EnsureSyncQueueTargetPathColumn(db);
    }

    private static void EnsureSyncQueueTargetPathColumn(ClientDbContext db)
    {
        try
        {
            bool hasColumn = db.Database.SqlQueryRaw<int>(
                "SELECT COUNT(*) FROM pragma_table_info('SyncQueue') WHERE name='TargetPath';")
                .ToList().FirstOrDefault() > 0;
            if (!hasColumn)
            {
                db.Database.ExecuteSqlRaw("ALTER TABLE SyncQueue ADD COLUMN TargetPath TEXT NULL;");
                Log.Information("旧客户端库 SyncQueue 已补 TargetPath 列");
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "SyncQueue TargetPath 列兼容检查失败（非致命）");
        }
    }

}
