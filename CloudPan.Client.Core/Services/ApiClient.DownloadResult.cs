namespace CloudPan.Client.Core.Services;

/// <summary>下载结果——包含服务端最后修改时间和 X-File-Hash 期望哈希值。</summary>
public class DownloadResult
{
    public string? LastModified { get; set; }
    public string? ExpectedHash { get; set; }
}
