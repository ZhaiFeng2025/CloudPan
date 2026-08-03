using CloudPan.Client.Core.Composition;
using CloudPan.Client.Core.Services;
using CloudPan.Contract;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CloudPan.Client.UI;

/// <summary>客户端入口：托盘常驻 + 管理窗口。启动编排（配置解析/DPAPI/建库/DI/连接检测）由 Client.Core 的 ClientBootstrap 承载，本类只做 UI 交互与装配（T-029）。</summary>
public static class Program
{
    public static string SyncRoot { get; internal set; } = "";
    public static string ServerUrl { get; internal set; } = $"http://localhost:{SpecPorts.HttpPort}";
    public static string Token { get; internal set; } = "";
    /// <summary>启动时连接检测结果（true = 离线），运行中由 WebSocket 事件更新。</summary>
    public static bool IsOffline { get; internal set; }

    [STAThread]
    // 同步 Main：所有初始化在 STA UI 线程执行，避免 async 延续在线程池跨线程操作 UI 控件（initForm.Close 死锁）
    public static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        // 0. 全局异常处理（防止未处理异常静默崩溃进程）
        Application.ThreadException += StartupFlow.OnThreadException;
        AppDomain.CurrentDomain.UnhandledException += StartupFlow.OnUnhandledException;
        TaskScheduler.UnobservedTaskException += StartupFlow.OnUnobservedTaskException;

        // 1. 解析并合并配置（CLI 参数优先；Token 经 DPAPI 解密）
        ClientStartupResult startup = ClientBootstrap.ResolveStartup(args);
        if (startup.TokenDecryptError != null)
        {
            StartupFlow.NotifyTokenDecryptFailed(startup.TokenDecryptError);
        }
        ServerUrl = startup.ServerUrl;
        SyncRoot = startup.SyncRoot;
        Token = startup.Token;

        // 2. 首次运行（信息缺失）：配置窗口 + DPAPI 保存（失败允许重试）
        if (string.IsNullOrEmpty(Token) || string.IsNullOrEmpty(ServerUrl) || string.IsNullOrEmpty(SyncRoot))
        {
            SyncRoot = string.IsNullOrEmpty(SyncRoot)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "CloudPan")
                : SyncRoot;
            if (!StartupFlow.ShowSetupAndSave())
            {
                return;
            }
        }

        // 3. 初始化进度提示（防止配置完成后到托盘出现之前的黑屏/闪烁）
        using Form initForm = StartupFlow.CreateInitForm();
        initForm.Show();
        Application.DoEvents();

        // 4. 启动装配（建目录/设备ID/日志/DI/建库）；失败提示并退出
        ClientBootstrap bootstrap = new ClientBootstrap(ServerUrl, SyncRoot, Token);
        try
        {
            bootstrap.Prepare();
        }
        catch (Exception ex)
        {
            StartupFlow.NotifyStartupFailed(ex.Message);
            return;
        }
        ILogger logger = bootstrap.Provider.GetRequiredService<ILoggerFactory>().CreateLogger("CloudPan.Client");

        // 5. 数据库完整性检查（PRAGMA quick_check）：损坏时由用户决定重建或退出
        if (bootstrap.CheckDatabaseIntegrity() == DatabaseIntegrityStatus.Corrupt)
        {
            if (StartupFlow.ConfirmDatabaseRebuild())
            {
                bootstrap.RebuildDatabase();
            }
            else
            {
                logger.LogInformation("用户选择退出程序");
                return;
            }
        }

        // 6. 连接检测（5 秒超时）+ 版本检查
        initForm.Controls.OfType<Label>().First().Text = "正在连接服务端...";
        Application.DoEvents();
        if (!bootstrap.HealthCheck().Connected)
        {
            IsOffline = true;
            if (StartupFlow.AskReconfigure(ServerUrl))
            {
                if (StartupFlow.ShowSetupAndSave())
                {
                    logger.LogInformation("配置已更新，重启客户端以应用新配置");
                }
                logger.LogInformation("用户重新配置后退出，请重新启动客户端");
                return;
            }
        }
        else
        {
            IsOffline = false;
        }

        // 7. 启动文件监控 + 同步引擎 + WebSocket + 托盘应用
        var watcher = bootstrap.Provider.GetRequiredService<FileWatcherService>();
        watcher.Start();
        var engine = bootstrap.Provider.GetRequiredService<SyncEngine>();
        var wsClient = bootstrap.Provider.GetRequiredService<WebSocketClient>();
        wsClient.OnConnected += OnWsConnected;
        wsClient.OnDisconnected += OnWsDisconnected;
        initForm.Close();

        Application.Run(new TrayAppContext(engine, wsClient));
    }

    /// <summary>WebSocket 连接建立：清除离线标志。</summary>
    private static void OnWsConnected() => IsOffline = false;

    /// <summary>WebSocket 断开：设置离线标志。</summary>
    private static void OnWsDisconnected() => IsOffline = true;
}
