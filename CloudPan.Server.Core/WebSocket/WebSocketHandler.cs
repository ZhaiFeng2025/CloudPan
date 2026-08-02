using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloudPan.Server.Data;
using CloudPan.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace CloudPan.Server.Services;

/// <summary>
/// WebSocket 连接管理器。
/// 管理设备连接池、认证、心跳、广播和在线状态。
/// </summary>
public class WebSocketHandler : IWebSocketHandler, IDisposable
{
    private readonly ConcurrentDictionary<string, WebSocketConnection> _connections = new();
    private readonly IDbContextFactory<CloudPanDbContext> _dbFactory;
    private readonly IMemoryCache _cache;
    private readonly ILogger<WebSocketHandler> _logger;
    private readonly System.Threading.Timer _heartbeatTimer;

    private static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PongTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan AuthTimeout = TimeSpan.FromSeconds(10);

    public int ActiveConnectionCount => _connections.Count;

    public WebSocketHandler(
        IDbContextFactory<CloudPanDbContext> dbFactory,
        IMemoryCache cache,
        ILogger<WebSocketHandler> logger)
    {
        _dbFactory = dbFactory;
        _cache = cache;
        _logger = logger;
        _heartbeatTimer = new System.Threading.Timer(CheckHeartbeats, null, PingInterval, PingInterval);
    }

    // ============================================================
    // 连接管理
    // ============================================================

    /// <inheritdoc />
    public async Task HandleConnectionAsync(WebSocket socket)
    {
        // 1. 等待认证消息
        byte[] buffer = new byte[4096];
        WebSocketReceiveResult result;
        using CancellationTokenSource authCts = new CancellationTokenSource(AuthTimeout);

        try
        {
            result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), authCts.Token);
        }
        catch (OperationCanceledException)
        {
            await SendJsonAsync(socket, new { type = WebSocketEvent.AuthError, message = "认证超时" });
            await CloseSafeAsync(socket, WebSocketCloseStatus.PolicyViolation, "auth timeout");
            return;
        }
        catch (WebSocketException)
        {
            return;
        }

        if (result.MessageType == WebSocketMessageType.Close)
        {
            await CloseSafeAsync(socket, WebSocketCloseStatus.NormalClosure, "closed before auth");
            return;
        }

        // 2. 解析认证 JSON（首条消息），获取 token + deviceId。
        // 认证模式 = 消息级（spec api.websocket.authMode=message），deviceId 由客户端在首条消息携带，
        // 不依赖 TokenAuthMiddleware 的 context.Items（修复 WS 认证必失败缺陷）。
        string? token = null;
        string? deviceId = null;
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
            await SendJsonAsync(socket, new { type = WebSocketEvent.AuthError, message = "无效的 JSON" });
            await CloseSafeAsync(socket, WebSocketCloseStatus.PolicyViolation, "invalid auth json");
            return;
        }

        if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(deviceId))
        {
            await SendJsonAsync(socket, new { type = WebSocketEvent.AuthError, message = "缺少 token 或 deviceId" });
            await CloseSafeAsync(socket, WebSocketCloseStatus.PolicyViolation, "missing token or device id");
            return;
        }

        // 4. 验证 Token
        string tokenHash = ComputeSha256(token);
        string? storedHash = await _cache.GetOrCreateAsync("token_hash_cache", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5);
            await using var db = await _dbFactory.CreateDbContextAsync();
            return await db.AppConfigs
                .Where(c => c.Key == "token_hash")
                .Select(c => c.Value)
                .FirstOrDefaultAsync();
        });

        if (storedHash == null || !string.Equals(tokenHash, storedHash, StringComparison.OrdinalIgnoreCase))
        {
            await SendJsonAsync(socket, new { type = WebSocketEvent.AuthError, message = "Token 无效" });
            await CloseSafeAsync(socket, WebSocketCloseStatus.PolicyViolation, "invalid token");
            return;
        }

        // 5. 认证成功
        await SendJsonAsync(socket, new { type = WebSocketEvent.AuthOk, deviceId });

        // 6. 注册连接 + 更新在线状态
        // 同设备重复连接时先移除并关闭旧连接，防止旧 Socket 泄漏
        if (_connections.TryRemove(deviceId, out var oldConn))
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
        _connections[deviceId] = connection;

        await UpdateDeviceOnlineAsync(deviceId, online: true);
        _logger.LogInformation("WebSocket 已连接: {DeviceId}", deviceId);

        // 7. 接收循环
        try
        {
            byte[] msgBuffer = new byte[8192];
            StringBuilder msgBuilder = new StringBuilder();
            while (socket.State == WebSocketState.Open)
            {
                msgBuilder.Clear();
                int totalBytes = 0;
                bool receivedClose = false;

                WebSocketReceiveResult msgResult;
                do
                {
                    msgResult = await socket.ReceiveAsync(new ArraySegment<byte>(msgBuffer), CancellationToken.None);

                    if (msgResult.MessageType == WebSocketMessageType.Close)
                    {
                        receivedClose = true;
                        break;
                    }

                    totalBytes += msgResult.Count;
                    if (totalBytes > 1024 * 1024)
                    {
                        _logger.LogError("WebSocket 消息超过 1MB 限制({Size} bytes)，即将关闭连接", totalBytes);
                        await CloseSafeAsync(socket, WebSocketCloseStatus.MessageTooBig, "message exceeds 1MB limit");
                        return;
                    }

                    msgBuilder.Append(Encoding.UTF8.GetString(msgBuffer, 0, msgResult.Count));
                }
                while (!msgResult.EndOfMessage && socket.State == WebSocketState.Open);

                if (receivedClose)
                {
                    break;
                }

                if (msgBuilder.Length > 0)
                {
                    HandleMessage(deviceId, msgBuilder.ToString());
                }
            }
        }
        catch (WebSocketException ex)
        {
            _logger.LogWarning(ex, "WebSocket 接收循环异常: {DeviceId}", deviceId);
        }
        finally
        {
            // 断开清理：仅当字典中仍是我（引用相同）时才移除，避免旧连接的 finally 误删新连接
            if (_connections.TryGetValue(deviceId, out var current) && ReferenceEquals(current, connection))
            {
                _connections.TryRemove(deviceId, out _);
                await UpdateDeviceOnlineAsync(deviceId, online: false);
            }
            else
            {
                _logger.LogDebug("设备 {DeviceId} 的连接已被新连接替换，跳过清理", deviceId);
            }
            _logger.LogInformation("WebSocket 已断开: {DeviceId}", deviceId);

            try { if (socket.State != WebSocketState.Closed)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "disconnect", CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "关闭 WebSocket 时发生异常: {DeviceId}", deviceId);
            }
        }
    }

    private void HandleMessage(string deviceId, string json)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            string? type = doc.RootElement.TryGetProperty("type", out var t) ? t.GetString() : null;

            if (type == WebSocketEvent.Pong && _connections.TryGetValue(deviceId, out var conn))
            {
                conn.LastPong = DateTime.UtcNow;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "处理 WebSocket 消息时发生异常: {DeviceId}={Message}", deviceId, json);
        }
    }

    // ============================================================
    // 广播
    // ============================================================

    /// <inheritdoc />
    public async Task BroadcastFileChangedAsync(string path, int version, string? excludeDeviceId = null)
    {
        await BroadcastAsync(new
        {
            type = WebSocketEvent.FileChanged,
            path,
            version,
            timestamp = DateTime.UtcNow.ToString("O")
        }, excludeDeviceId);
    }

    /// <inheritdoc />
    public async Task BroadcastFileDeletedAsync(string path, string? excludeDeviceId = null)
    {
        await BroadcastAsync(new
        {
            type = WebSocketEvent.FileDeleted,
            path,
            timestamp = DateTime.UtcNow.ToString("O")
        }, excludeDeviceId);
    }

    /// <inheritdoc />
    public async Task BroadcastFileRenamedAsync(string oldPath, string newPath, string? excludeDeviceId = null)
    {
        await BroadcastAsync(new
        {
            type = WebSocketEvent.FileRenamed,
            path = newPath,
            data = new { oldPath },
            timestamp = DateTime.UtcNow.ToString("O")
        }, excludeDeviceId);
    }

    private async Task BroadcastAsync(object payload, string? excludeDeviceId)
    {
        string json = JsonSerializer.Serialize(payload);
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        ArraySegment<byte> segment = new ArraySegment<byte>(bytes);

        foreach (var (deviceId, conn) in _connections)
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

    // ============================================================
    // 心跳
    // ============================================================

    /// <summary>防止心跳检测重叠执行的轻量锁。</summary>
    private int _heartbeatRunning;

    private void CheckHeartbeats(object? state)
    {
        // 防止上一轮心跳尚未完成时 Timer 再次触发导致重叠执行
        if (Interlocked.CompareExchange(ref _heartbeatRunning, 1, 0) != 0)
        {
            _logger.LogWarning("心跳检测跳过——上一轮尚未完成");
            return;
        }

        // 使用 Task.Run 包裹异步逻辑，避免 async void 异常崩溃进程（符合 CLAUDE.md 7.2 规范）
        Task.Run(async () =>
        {
            try
            {
                var now = DateTime.UtcNow;
                foreach (var (deviceId, conn) in _connections)
                {
                    try
                    {
                        if (conn.Socket.State != WebSocketState.Open)
                        {
                            _connections.TryRemove(deviceId, out _);
                            await UpdateDeviceOnlineAsync(deviceId, false);
                            continue;
                        }

                        // Pong 超时检测
                        if (now - conn.LastPong > PongTimeout)
                        {
                            _logger.LogWarning("WebSocket 心跳超时: {DeviceId}", deviceId);
                            _connections.TryRemove(deviceId, out _);
                            await CloseSafeAsync(conn.Socket, WebSocketCloseStatus.NormalClosure, "heartbeat timeout");
                            await UpdateDeviceOnlineAsync(deviceId, false);
                            continue;
                        }

                        // 发送 Ping
                        await SendJsonAsync(conn.Socket, new { type = WebSocketEvent.Ping });
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "心跳处理异常: {DeviceId}={Error}", deviceId, ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "心跳检测整体异常");
            }
            finally
            {
                Interlocked.Exchange(ref _heartbeatRunning, 0);
            }
        });
    }

    // ============================================================
    // 工具方法
    // ============================================================

    private async Task UpdateDeviceOnlineAsync(string deviceId, bool online)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var device = await db.Devices.FindAsync(deviceId);
            if (device != null)
            {
                device.Online = online ? 1 : 0;
                device.LastSeen = DateTime.UtcNow.ToString("O");
            }
            else
            {
                // 自动注册
                db.Devices.Add(new Models.Device
                {
                    Id = deviceId,
                    Name = $"设备-{deviceId[..Math.Min(8, deviceId.Length)]}",
                    Person = null,
                    LastSeen = DateTime.UtcNow.ToString("O"),
                    Online = online ? 1 : 0,
                    RegisteredAt = DateTime.UtcNow.ToString("O")
                });
            }
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "更新设备在线状态失败: {DeviceId}", deviceId);
        }
    }

    private async Task SendJsonAsync(WebSocket socket, object payload)
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

    private async Task CloseSafeAsync(WebSocket socket, WebSocketCloseStatus status, string description)
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

    private static string ComputeSha256(string input)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public void Dispose()
    {
        _heartbeatTimer.Dispose();
        foreach (var (_, conn) in _connections)
        {
            try { conn.Socket.Dispose(); } catch (Exception ex)
            {
                _logger.LogWarning(ex, "释放 WebSocket 资源时发生异常");
            }
        }
        _connections.Clear();
    }

    private class WebSocketConnection
    {
        public WebSocket Socket { get; set; } = null!;
        public string DeviceId { get; set; } = "";
        public DateTime ConnectedAt { get; set; }
        public DateTime LastPong { get; set; }
    }
}
