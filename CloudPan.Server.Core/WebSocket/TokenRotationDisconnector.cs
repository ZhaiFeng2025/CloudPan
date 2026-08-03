using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CloudPan.Server.Core;

/// <summary>
/// Token 轮换断开接线（T-072）：订阅 TokenService.TokenRotated 事件，轮换需断开连接时执行
/// IWebSocketHandler.DisconnectAllAsync。替代 TokenService 内 _services.GetRequiredService 服务定位器
/// 延迟解析（原为打破 TokenService ⇄ WebSocketHandler 构造期循环依赖）。
/// 以 IHostedService 挂载：宿主启动即建立订阅、停止即取消订阅（生命周期托管，Singleton 无事件泄漏）。
/// WebSocketHandler 聚合行数已达 T-070 文档上限，故订阅独立于该类型承载。
/// </summary>
public sealed class TokenRotationDisconnector : IHostedService
{
    private readonly ITokenService _tokenService;
    private readonly IWebSocketHandler _wsHandler;
    private readonly ILogger<TokenRotationDisconnector> _logger;

    public TokenRotationDisconnector(
        ITokenService tokenService,
        IWebSocketHandler wsHandler,
        ILogger<TokenRotationDisconnector> logger)
    {
        _tokenService = tokenService;
        _wsHandler = wsHandler;
        _logger = logger;
    }

    /// <summary>宿主启动时订阅 Token 轮换事件（具名方法订阅，可在 StopAsync 取消）。</summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _tokenService.TokenRotated += OnTokenRotated;
        return Task.CompletedTask;
    }

    /// <summary>宿主停止时取消订阅，防止 TokenService（Singleton）持引用泄漏本实例。</summary>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _tokenService.TokenRotated -= OnTokenRotated;
        return Task.CompletedTask;
    }

    /// <summary>Token 轮换事件处理：断开所有已连接设备。全量 try-catch——Token 已轮换成功，断开失败不应使轮换报错（CLAUDE.md 7.2）。</summary>
    private async Task OnTokenRotated(string reason)
    {
        try
        {
            await _wsHandler.DisconnectAllAsync(reason);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Token 轮换断开连接异常: {Reason}", reason);
        }
    }
}
