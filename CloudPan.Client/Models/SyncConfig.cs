namespace CloudPan.Client.Models;

/// <summary>
/// 客户端同步配置——供 DI 容器注入，消除原始类型（string syncRoot）的注入歧义。
/// </summary>
public record SyncConfig
{
    /// <summary>本地同步根目录的绝对路径。</summary>
    public string SyncRoot { get; init; } = "";

    /// <summary>服务端地址，如 http://localhost:8443。</summary>
    public string ServerUrl { get; init; } = "http://localhost:8443";

    /// <summary>家庭共享 Token（64 字符十六进制）。</summary>
    public string Token { get; init; } = "";

    /// <summary>设备 GUID，首次连接时生成，持久化存储。</summary>
    public string DeviceId { get; init; } = "";
}
