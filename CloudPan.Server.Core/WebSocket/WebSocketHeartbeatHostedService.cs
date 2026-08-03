using CloudPan.Contract;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CloudPan.Server.Core;

/// <summary>
/// WebSocket 心跳托管服务：按 SpecConfig.PingIntervalSeconds 周期调用 IWebSocketHandler 心跳检测
/// （Pong 超时清理/发送 Ping/在线状态维护）。
/// T-057：替代 WebSocketHandler 内裸 Timer 自调度（R-A6 定时任务用 IHostedService），
/// 间隔读 SpecConfig 单源禁止字面量；周期循环用 PeriodicTimer，回调全量 try-catch（CLAUDE.md 7.2）。
/// </summary>
public sealed class WebSocketHeartbeatHostedService : BackgroundService
{
    private readonly IWebSocketHandler _handler;
    private readonly ILogger<WebSocketHeartbeatHostedService> _logger;

    /// <summary>心跳周期，读 SpecConfig 单源（PingIntervalSeconds）。</summary>
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(SpecConfig.PingIntervalSeconds);

    public WebSocketHeartbeatHostedService(
        IWebSocketHandler handler,
        ILogger<WebSocketHeartbeatHostedService> logger)
    {
        _handler = handler;
        _logger = logger;
    }

    /// <summary>周期执行心跳检测；宿主停止时经 stoppingToken 正常退出。</summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("WebSocket 心跳托管服务已启动（间隔 {IntervalSeconds}s）", SpecConfig.PingIntervalSeconds);
        using var timer = new PeriodicTimer(Interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await _handler.CheckHeartbeatsAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "WebSocket 心跳检测异常");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 宿主停止：PeriodicTimer.WaitForNextTickAsync 抛 OperationCanceledException，正常退出
        }
    }
}
