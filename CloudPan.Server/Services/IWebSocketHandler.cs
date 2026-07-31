namespace CloudPan.Server.Services;

/// <summary>
/// WebSocket 连接管理服务接口。
/// </summary>
public interface IWebSocketHandler
{
    /// <summary>接受 WebSocket 连接，处理认证和接收循环。</summary>
    Task HandleConnectionAsync(System.Net.WebSockets.WebSocket socket, HttpContext context);

    /// <summary>广播文件变更事件（排除发送设备）。</summary>
    Task BroadcastFileChangedAsync(string path, int version, string? excludeDeviceId = null);

    /// <summary>广播文件删除事件（排除发送设备）。</summary>
    Task BroadcastFileDeletedAsync(string path, string? excludeDeviceId = null);

    /// <summary>广播文件重命名事件（排除发送设备）。</summary>
    Task BroadcastFileRenamedAsync(string oldPath, string newPath, string? excludeDeviceId = null);

    /// <summary>当前活跃连接数。</summary>
    int ActiveConnectionCount { get; }
}
