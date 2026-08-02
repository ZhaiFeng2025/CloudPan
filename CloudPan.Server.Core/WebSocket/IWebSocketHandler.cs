namespace CloudPan.Server.Core;

/// <summary>
/// WebSocket 连接管理服务接口。
/// </summary>
public interface IWebSocketHandler
{
    /// <summary>接受 WebSocket 连接，处理认证（首条消息解析 token + deviceId，消息级认证）和接收循环。</summary>
    Task HandleConnectionAsync(System.Net.WebSockets.WebSocket socket);

    /// <summary>广播文件变更事件（排除发送设备）。</summary>
    Task BroadcastFileChangedAsync(string path, int version, string? excludeDeviceId = null);

    /// <summary>广播文件删除事件（排除发送设备）。</summary>
    Task BroadcastFileDeletedAsync(string path, string? excludeDeviceId = null);

    /// <summary>广播文件重命名事件（排除发送设备）。</summary>
    Task BroadcastFileRenamedAsync(string oldPath, string newPath, string? excludeDeviceId = null);

    /// <summary>断开所有已连接设备（Token 轮换可选步骤）。逐个容错，不中断整体。</summary>
    Task DisconnectAllAsync(string reason);

    /// <summary>当前活跃连接数。</summary>
    int ActiveConnectionCount { get; }
}
