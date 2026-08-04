using System.Net.WebSockets;
using CloudPan.Contract;
using Microsoft.Extensions.Logging;

namespace CloudPan.Server.Core;

/// <summary>WebSocketHandler 部分实现：连接管理（Token 轮换断开）与心跳检测。发送/关闭/在线状态工具在 WebSocketConnectionRegistry（T-111）。</summary>
public partial class WebSocketHandler
{
    // ============================================================
    // 连接管理（Token 轮换用）
    // ============================================================

    /// <inheritdoc />
    public async Task DisconnectAllAsync(string reason)
    {
        // 快照遍历避免迭代时修改字典
        foreach (var (deviceId, conn) in _registry.Connections.ToArray())
        {
            if (_registry.Connections.TryRemove(deviceId, out _))
            {
                try
                {
                    await _registry.CloseSafeAsync(conn.Socket, WebSocketCloseStatus.PolicyViolation, reason);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Token 轮换断开连接失败: {DeviceId}", deviceId);
                }
                await _registry.UpdateDeviceOnlineAsync(deviceId, online: false);
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
            foreach (var (deviceId, conn) in _registry.Connections)
            {
                try
                {
                    if (conn.Socket.State != WebSocketState.Open)
                    {
                        _registry.Connections.TryRemove(deviceId, out _);
                        await _registry.UpdateDeviceOnlineAsync(deviceId, false);
                        continue;
                    }

                    // Pong 超时检测（超时阈值读 SpecConfig.PongTimeoutSeconds）
                    if (now - conn.LastPong > PongTimeout)
                    {
                        _logger.LogWarning("WebSocket 心跳超时: {DeviceId}", deviceId);
                        _registry.Connections.TryRemove(deviceId, out _);
                        await _registry.CloseSafeAsync(conn.Socket, WebSocketCloseStatus.NormalClosure, "heartbeat timeout");
                        await _registry.UpdateDeviceOnlineAsync(deviceId, false);
                        continue;
                    }

                    // 发送 Ping
                    await _registry.SendJsonAsync(conn.Socket, new { type = WebSocketEvent.Ping });
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
}
