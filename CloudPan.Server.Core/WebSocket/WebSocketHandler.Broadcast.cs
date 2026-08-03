using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using CloudPan.Contract;
using Microsoft.Extensions.Logging;

namespace CloudPan.Server.Core;

/// <summary>WebSocketHandler 部分实现：文件变更/删除/重命名广播。</summary>
public partial class WebSocketHandler
{
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
}
