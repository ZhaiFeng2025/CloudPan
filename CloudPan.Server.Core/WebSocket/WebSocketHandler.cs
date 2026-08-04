using System.Net.WebSockets;
using CloudPan.Contract;
using Microsoft.Extensions.Logging;

namespace CloudPan.Server.Core;

/// <summary>
/// WebSocket 连接管理器。
/// 管理设备连接池、认证、心跳、广播和在线状态。
/// 认证（Token 校验与设备注册）经 ITokenService（F-25/T-025 单一事实来源），与 HTTP 中间件共用。
/// T-111：连接池（WebSocketConnectionRegistry）与会话生命周期（WebSocketSession）外提为 internal 协作类，
/// 聚合 ≤400；公开 API 与并发语义零变化。
/// </summary>
public partial class WebSocketHandler : IWebSocketHandler, IDisposable
{
    private static readonly TimeSpan PongTimeout = TimeSpan.FromSeconds(SpecConfig.PongTimeoutSeconds);

    private readonly WebSocketConnectionRegistry _registry;
    private readonly ITokenService _tokenService;
    private readonly ILogger<WebSocketHandler> _logger;

    public int ActiveConnectionCount => _registry.Connections.Count;

    public WebSocketHandler(
        ITokenService tokenService,
        ILogger<WebSocketHandler> logger)
    {
        _tokenService = tokenService;
        _logger = logger;
        _registry = new WebSocketConnectionRegistry(tokenService, logger);
    }

    // ============================================================
    // 连接管理
    // ============================================================

    /// <inheritdoc />
    public async Task HandleConnectionAsync(WebSocket socket)
    {
        // 认证/接收循环/断连清理三阶段由 WebSocketSession 承载（T-111），每阶段独立可单测
        var session = new WebSocketSession(_registry, _tokenService, _logger, socket);
        await session.RunAsync();
    }

    public void Dispose()
    {
        _registry.DisposeAll();
    }
}
