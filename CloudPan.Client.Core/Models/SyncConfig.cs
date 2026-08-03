using CloudPan.Contract;

namespace CloudPan.Client.Core.Models;

/// <summary>
/// 客户端同步配置——供 DI 容器注入，消除原始类型（string syncRoot）的注入歧义。
/// 边界（T-043）：SyncConfig 是进程内同步运行参数（启动时由 ClientBootstrap 从持久化配置与
/// CLI 参数/解密 Token 装配），不落盘、不静态可变；持久化配置统一由 ClientConfig 承担（唯一读盘入口
/// 为 ResolveStartup，读盘一次）。两者字段有重叠（SyncRoot/ServerUrl/限速/SelectedPaths），
/// SyncConfig 是该次运行的生效快照，ClientConfig 是磁盘上的可保存源。
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
