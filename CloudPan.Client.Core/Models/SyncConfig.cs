using CloudPan.Contract;

namespace CloudPan.Client.Core.Models;

/// <summary>
/// 客户端同步配置——供 DI 容器注入，消除原始类型（string syncRoot）的注入歧义。
/// </summary>
public record SyncConfig
{
    /// <summary>本地同步根目录的绝对路径。</summary>
    public string SyncRoot { get; init; } = "";

    /// <summary>服务端地址，如 http://localhost:8443。</summary>
    public string ServerUrl { get; init; } = $"http://localhost:{SpecPorts.HttpPort}";

    /// <summary>家庭共享 Token（64 字符十六进制）。</summary>
    public string Token { get; init; } = "";

    /// <summary>设备 GUID，首次连接时生成，持久化存储。</summary>
    public string DeviceId { get; init; } = "";

    /// <summary>上传速率限制（字节/秒），0 = 不限速。</summary>
    public long UploadSpeedLimitBps { get; init; } = 0;

    /// <summary>下载速率限制（字节/秒），0 = 不限速。</summary>
    public long DownloadSpeedLimitBps { get; init; } = 0;

    /// <summary>已选择同步的文件夹路径列表（默认全选 "/"）。</summary>
    public List<string> SelectedPaths { get; init; } = new() { "/" };
}
