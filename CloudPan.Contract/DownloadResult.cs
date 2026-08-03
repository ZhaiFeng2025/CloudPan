namespace CloudPan.Contract;

/// <summary>
/// 下载结果——包含服务端最后修改时间（X-File-Modified）与期望哈希（X-File-Hash）。
/// 属传输协议抽象（CLAUDE.md 规则 7），归契约层；由生成的 IApiClient.DownloadAsync 返回，
/// 供 ApiClient 实现与调用方在下载后做 SHA-256 校验。
/// </summary>
public class DownloadResult
{
    public string? LastModified { get; set; }
    public string? ExpectedHash { get; set; }
}
