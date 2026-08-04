using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace CloudPan.Server.Core;

/// <summary>
/// WebSocket 连接注册表协作类（T-111）：连接池、设备重复替换、在线状态、发送/关闭/广播工具。
/// 逻辑从 WebSocketHandler 外提，公开 API 零变化；internal 类型不参与聚合行数门禁。
/// </summary>
internal sealed class WebSocketConnectionRegistry
{
    /// <summary>设备连接池：deviceId → 连接。ConcurrentDictionary 保证并发读写安全（CLAUDE.md 7.4）。</summary>
    internal ConcurrentDictionary<string, WebSocketConnection> Connections { get; } = new();

    private readonly ITokenService _tokenService;
    private readonly ILogger<WebSocketHandler> _logger;

    internal WebSocketConnectionRegistry(ITokenService tokenService, ILogger<WebSocketHandler> logger)
    {
        _tokenService = tokenService;
        _logger = logger;
    }

    /// <summary>
    /// 注册连接并更新在线状态。同设备重复连接时先移除并关闭旧连接（防旧 Socket 泄漏），
    /// 与 WebSocketHandler.HandleConnectionAsync 原语义一致（T-111 拆分不改变并发行为）。
    /// </summary>
    internal async Task<WebSocketConnection> RegisterAsync(WebSocket socket, string deviceId)
    {
        if (Connections.TryRemove(deviceId, out var oldConn))
        {
            _logger.LogWarning("设备重复连接，正在关闭旧连接: {DeviceId}", deviceId);
            await CloseSafeAsync(oldConn.Socket, WebSocketCloseStatus.NormalClosure, "replaced by new connection");
        }

        var connection = new WebSocketConnection
        {
            Socket = socket,
            DeviceId = deviceId,
            ConnectedAt = DateTime.UtcNow,
            LastPong = DateTime.UtcNow
        };
        Connections[deviceId] = connection;

        await UpdateDeviceOnlineAsync(deviceId, online: true);
        return connection;
    }

    /// <summary>设备在线状态维护（经 ITokenService 单一事实来源，异常容错不抛出）。</summary>
    internal async Task UpdateDeviceOnlineAsync(string deviceId, bool online)
    {
        try
        {
            await _tokenService.EnsureDeviceAsync(deviceId, online);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "更新设备在线状态失败: {DeviceId}", deviceId);
        }
    }

    /// <summary>发送一条 JSON 文本消息（单条发送，异常容错不抛出）。</summary>
    internal async Task SendJsonAsync(WebSocket socket, object payload)
    {
        string json = JsonSerializer.Serialize(payload);
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        try
        {
            await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "WebSocket 发送消息失败");
        }
    }

    /// <summary>安全关闭连接：仅 Open/CloseReceived 状态尝试，异常容错不抛出。</summary>
    internal async Task CloseSafeAsync(WebSocket socket, WebSocketCloseStatus status, string description)
    {
        try
        {
            if (socket.State == WebSocketState.Open || socket.State == WebSocketState.CloseReceived)
            {
                await socket.CloseAsync(status, description, CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "WebSocket 关闭连接失败: {Status}/{Desc}", status, description);
        }
    }

    /// <summary>向所有已连接设备广播（排除发送设备），逐个容错不中断整体。</summary>
    internal async Task BroadcastAsync(object payload, string? excludeDeviceId)
    {
        string json = JsonSerializer.Serialize(payload);
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        ArraySegment<byte> segment = new ArraySegment<byte>(bytes);

        foreach (var (deviceId, conn) in Connections)
        {
            if (deviceId == excludeDeviceId)
            {
                continue;
            }

            if (conn.Socket.State == WebSocketState.Open)
            {
                try
                {
                    await conn.Socket.SendAsync(segment, WebSocketMessageType.Text, true, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "广播消息发送失败: {DeviceId}", deviceId);
                }
            }
        }
    }

    /// <summary>释放全部连接资源并清空连接池（WebSocketHandler.Dispose 委托）。</summary>
    internal void DisposeAll()
    {
        foreach (var (_, conn) in Connections)
        {
            try
            {
                conn.Socket.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "释放 WebSocket 资源时发生异常");
            }
        }
        Connections.Clear();
    }
}

/// <summary>单个 WebSocket 连接的状态（连接池条目）。</summary>
internal sealed class WebSocketConnection
{
    internal WebSocket Socket { get; set; } = null!;
    internal string DeviceId { get; set; } = "";
    internal DateTime ConnectedAt { get; set; }
    internal DateTime LastPong { get; set; }
}
