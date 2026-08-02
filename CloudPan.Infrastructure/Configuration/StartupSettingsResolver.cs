using CloudPan.Contract;

namespace CloudPan.Infrastructure.Configuration;

/// <summary>
/// 启动期设置合并器：CLI 参数（--Port/--SyncRoot）优先，其次 server-settings.json，最后默认值。
/// Host 与 Server.UI 共用同一逻辑，避免两处漂移导致端口/目录认知不一致。
/// 故意不依赖 IConfiguration——Infrastructure 不引入配置包，由调用方先解析 CLI 值再传入。
/// </summary>
public static class StartupSettingsResolver
{
    public static (string SyncRoot, int Port) Resolve(
        string? cliSyncRoot, int? cliPort, string defaultSyncRoot)
    {
        BootstrapSettings? bootstrap = ServerSettingsFile.Load();

        string syncRoot = cliSyncRoot
                       ?? bootstrap?.SyncRoot
                       ?? defaultSyncRoot;

        int port = cliPort
               ?? bootstrap?.Port
               ?? SpecPorts.HttpPort;

        return (syncRoot, port);
    }
}
