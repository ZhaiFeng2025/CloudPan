using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace CloudPan.Infrastructure.Logging;

/// <summary>
/// Serilog 装配唯一来源（两端共用，T-096）。WriteTo.Console + WriteTo.File 与输出模板在此集中定义，
/// 改日志策略/级别只改此处；Client/Server 不再各自手写 LoggerConfiguration，避免装配分叉。
/// </summary>
public static class SerilogFactory
{
    /// <summary>
    /// 装配全局 Serilog 日志器：控制台 + 每日滚动文件（保留 7 份）。
    /// </summary>
    /// <param name="logFilePath">文件日志路径（含滚动文件名模板，如 server-.log）。</param>
    /// <param name="minimumLevelOverrides">命名空间 → 最低级别覆盖（如 Microsoft→Warning，服务端用于压制框架日志）。</param>
    public static Logger CreateLogger(string logFilePath, params (string Source, LogEventLevel MinimumLevel)[] minimumLevelOverrides)
    {
        LoggerConfiguration configuration = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Console(
                outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
            .WriteTo.File(
                logFilePath,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}");

        foreach (var o in minimumLevelOverrides)
        {
            configuration = configuration.MinimumLevel.Override(o.Source, o.MinimumLevel);
        }

        return configuration.CreateLogger();
    }
}
