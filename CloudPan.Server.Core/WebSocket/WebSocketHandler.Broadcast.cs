using CloudPan.Contract;

namespace CloudPan.Server.Core;

/// <summary>WebSocketHandler 部分实现：文件变更/删除/重命名广播。广播发送逻辑在 WebSocketConnectionRegistry（T-111）。</summary>
public partial class WebSocketHandler
{
    // ============================================================
    // 广播
    // ============================================================

    /// <inheritdoc />
    public async Task BroadcastFileChangedAsync(string path, int version, string? excludeDeviceId = null)
    {
        await _registry.BroadcastAsync(new
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
        await _registry.BroadcastAsync(new
        {
            type = WebSocketEvent.FileDeleted,
            path,
            timestamp = DateTime.UtcNow.ToString("O")
        }, excludeDeviceId);
    }

    /// <inheritdoc />
    public async Task BroadcastFileRenamedAsync(string oldPath, string newPath, string? excludeDeviceId = null)
    {
        await _registry.BroadcastAsync(new
        {
            type = WebSocketEvent.FileRenamed,
            path = newPath,
            data = new { oldPath },
            timestamp = DateTime.UtcNow.ToString("O")
        }, excludeDeviceId);
    }
}
