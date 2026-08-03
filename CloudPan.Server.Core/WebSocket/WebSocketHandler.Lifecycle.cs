using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using CloudPan.Contract;
using Microsoft.Extensions.Logging;

namespace CloudPan.Server.Core;

/// <summary>WebSocketHandler 部分实现：连接管理（Token 轮换断开）、心跳检测与发送/关闭工具。</summary>
public partial class WebSocketHandler
{
    // ============================================================
    // 连接管理（Token 轮换用）
    // ============================================================

    /// <inheritdoc />
    public async Task DisconnectAllAsync(string reason)
    {
        // 快照遍历避免迭代时修改字典
        foreach (var (deviceId, conn) in _connections.ToArray())
        {
            if (_connections.TryRemove(deviceId, out _))
            {
                try
                {
                    await CloseSafeAsync(conn.Socket, WebSocketCloseStatus.PolicyViolation, reason);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Token 轮换断开连接失败: {DeviceId}", deviceId);
                }
                await UpdateDeviceOnlineAsync(deviceId, online: false);
            }
        }
    }

    // ============================================================
    // 心跳
    // ============================================================

    /// <summary>防止心跳检测重叠执行的轻量锁。</summary>
    private int _heartbeatRunning;

    /// <summary>
    /// 心跳检测：Pong 超时清理、发送 Ping、维护设备在线状态。
    /// 由 WebSocketHeartbeatHostedService 按 SpecConfig.PingIntervalSeconds 周期调用（T-057），
    /// 本类不再内置裸 Timer；异步安全经 Interlocked 防重入 + 全量 try-catch（CLAUDE.md 7.2）。
    /// </summary>
    public async Task CheckHeartbeatsAsync()
    {
        // 防止上一轮心跳尚未完成时再次触发导致重叠执行
        if (Interlocked.CompareExchange(ref _heartbeatRunning, 1, 0) != 0)
        {
            _logger.LogWarning("心跳检测跳过——上一轮尚未完成");
            return;
        }

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

                    // Pong 超时检测（超时阈值读 SpecConfig.PongTimeoutSeconds）
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
    }

    // ============================================================
    // 工具方法
    // ============================================================

    private async Task UpdateDeviceOnlineAsync(string deviceId, bool online)
    {
        try
        {
            // 设备自动注册 + LastSeen/Online 维护收敛到 ITokenService（T-025 单一事实来源）
            await _tokenService.EnsureDeviceAsync(deviceId, online);
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
}
