using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using CloudPan.Contract;
using Microsoft.Extensions.Logging;

namespace CloudPan.Server.Core;

/// <summary>
/// 单个 WebSocket 连接会话协作类（T-111）：认证/接收循环/断连清理三阶段各自独立、可单测。
/// 逻辑从 WebSocketHandler.HandleConnectionAsync（239 行单方法）外提；内部方法可直接单测。
/// 并发语义与 CLUADE.md 7.4 竞态路径与原实现一致（重复设备替换/1MB 限制/finally 断连清理）。
/// </summary>
internal sealed class WebSocketSession
{
    private static readonly TimeSpan AuthTimeout = TimeSpan.FromSeconds(10);

    private readonly WebSocketConnectionRegistry _registry;
    private readonly ITokenService _tokenService;
    private readonly ILogger<WebSocketHandler> _logger;
    private readonly WebSocket _socket;

    internal WebSocketSession(
        WebSocketConnectionRegistry registry,
        ITokenService tokenService,
        ILogger<WebSocketHandler> logger,
        WebSocket socket)
    {
        _registry = registry;
        _tokenService = tokenService;
        _logger = logger;
        _socket = socket;
    }

    /// <summary>运行完整会话生命周期：认证 → 接收循环 → 断连清理（finally 保证清理始终执行）。</summary>
    internal async Task RunAsync()
    {
        WebSocketConnection? connection = await AuthenticateAsync();
        if (connection == null)
        {
            return; // 认证失败已发送错误并关闭连接
        }

        try
        {
            await ReceiveLoopAsync(connection);
        }
        finally
        {
            await CleanupAsync(connection);
        }
    }

    /// <summary>
    /// 阶段一：等待并解析认证消息（首条消息携带 token + deviceId）、校验 Token、注册连接。
    /// 设备重复时替换并关闭旧连接；任一失败路径发送 auth_error 并关闭连接，返回 null。
    /// </summary>
    internal async Task<WebSocketConnection?> AuthenticateAsync()
    {
        byte[] buffer = new byte[4096];
        WebSocketReceiveResult result;
        using CancellationTokenSource authCts = new CancellationTokenSource(AuthTimeout);

        try
        {
            result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), authCts.Token);
        }
        catch (OperationCanceledException)
        {
            await _registry.SendJsonAsync(_socket, new { type = WebSocketEvent.AuthError, message = "认证超时" });
            await _registry.CloseSafeAsync(_socket, WebSocketCloseStatus.PolicyViolation, "auth timeout");
            return null;
        }
        catch (WebSocketException)
        {
            return null;
        }

        if (result.MessageType == WebSocketMessageType.Close)
        {
            await _registry.CloseSafeAsync(_socket, WebSocketCloseStatus.NormalClosure, "closed before auth");
            return null;
        }

        // 解析认证 JSON，获取 token + deviceId（认证模式 = 消息级，spec api.websocket.authMode=message）
        string? token;
        string? deviceId;
        try
        {
            string json = Encoding.UTF8.GetString(buffer, 0, result.Count);
            using JsonDocument doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            token = root.TryGetProperty("token", out var t) ? t.GetString() : null;
            deviceId = root.TryGetProperty("deviceId", out var d) ? d.GetString() : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "解析认证 JSON 异常");
            await _registry.SendJsonAsync(_socket, new { type = WebSocketEvent.AuthError, message = "无效的 JSON" });
            await _registry.CloseSafeAsync(_socket, WebSocketCloseStatus.PolicyViolation, "invalid auth json");
            return null;
        }

        if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(deviceId))
        {
            await _registry.SendJsonAsync(_socket, new { type = WebSocketEvent.AuthError, message = "缺少 token 或 deviceId" });
            await _registry.CloseSafeAsync(_socket, WebSocketCloseStatus.PolicyViolation, "missing token or device id");
            return null;
        }

        // 验证 Token（经 ITokenService 单一事实来源：SHA-256 比对 + 5 分钟内存缓存，与 HTTP 中间件一致）
        TokenValidationResult validation = await _tokenService.ValidateTokenAsync(token);
        if (validation != TokenValidationResult.Valid)
        {
            await _registry.SendJsonAsync(_socket, new { type = WebSocketEvent.AuthError, message = "Token 无效" });
            await _registry.CloseSafeAsync(_socket, WebSocketCloseStatus.PolicyViolation, "invalid token");
            return null;
        }

        // 认证成功：发送 auth_ok → 注册连接（重复设备替换旧连接）
        await _registry.SendJsonAsync(_socket, new { type = WebSocketEvent.AuthOk, deviceId });
        var connection = await _registry.RegisterAsync(_socket, deviceId);
        _logger.LogInformation("WebSocket 已连接: {DeviceId}", deviceId);
        return connection;
    }

    /// <summary>阶段二：接收循环——聚合分片、1MB 上限、Pong 心跳应答。socket 关闭或异常时退出。</summary>
    internal async Task ReceiveLoopAsync(WebSocketConnection connection)
    {
        byte[] msgBuffer = new byte[8192];
        StringBuilder msgBuilder = new StringBuilder();
        try
        {
            while (_socket.State == WebSocketState.Open)
            {
                msgBuilder.Clear();
                int totalBytes = 0;
                bool receivedClose = false;

                WebSocketReceiveResult msgResult;
                do
                {
                    msgResult = await _socket.ReceiveAsync(new ArraySegment<byte>(msgBuffer), CancellationToken.None);

                    if (msgResult.MessageType == WebSocketMessageType.Close)
                    {
                        receivedClose = true;
                        break;
                    }

                    totalBytes += msgResult.Count;
                    if (totalBytes > 1024 * 1024)
                    {
                        _logger.LogError("WebSocket 消息超过 1MB 限制({Size} bytes)，即将关闭连接", totalBytes);
                        await _registry.CloseSafeAsync(_socket, WebSocketCloseStatus.MessageTooBig, "message exceeds 1MB limit");
                        return;
                    }

                    msgBuilder.Append(Encoding.UTF8.GetString(msgBuffer, 0, msgResult.Count));
                }
                while (!msgResult.EndOfMessage && _socket.State == WebSocketState.Open);

                if (receivedClose)
                {
                    break;
                }

                if (msgBuilder.Length > 0)
                {
                    HandleMessage(connection.DeviceId, msgBuilder.ToString());
                }
            }
        }
        catch (WebSocketException ex)
        {
            _logger.LogWarning(ex, "WebSocket 接收循环异常: {DeviceId}", connection.DeviceId);
        }
    }

    /// <summary>阶段三：断连清理——仅当连接池中仍是我（引用相同）时才移除，避免旧连接的 finally 误删新连接。</summary>
    internal async Task CleanupAsync(WebSocketConnection connection)
    {
        if (_registry.Connections.TryGetValue(connection.DeviceId, out var current) && ReferenceEquals(current, connection))
        {
            _registry.Connections.TryRemove(connection.DeviceId, out _);
            await _registry.UpdateDeviceOnlineAsync(connection.DeviceId, online: false);
        }
        else
        {
            _logger.LogDebug("设备 {DeviceId} 的连接已被新连接替换，跳过清理", connection.DeviceId);
        }
        _logger.LogInformation("WebSocket 已断开: {DeviceId}", connection.DeviceId);

        try
        {
            if (_socket.State != WebSocketState.Closed)
            {
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "disconnect", CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "关闭 WebSocket 时发生异常: {DeviceId}", connection.DeviceId);
        }
    }

    /// <summary>处理一条业务消息：Pong 心跳应答更新 LastPong（其余类型忽略，异常容错）。</summary>
    private void HandleMessage(string deviceId, string json)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            string? type = doc.RootElement.TryGetProperty("type", out var t) ? t.GetString() : null;

            if (type == WebSocketEvent.Pong && _registry.Connections.TryGetValue(deviceId, out var conn))
            {
                conn.LastPong = DateTime.UtcNow;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "处理 WebSocket 消息时发生异常: {DeviceId}={Message}", deviceId, json);
        }
    }
}
