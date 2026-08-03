namespace CloudPan.Contract;

/// <summary>
/// IApiClient 客户端运行时配置方法（非 HTTP 端点签名，不属 shared-spec.json 契约）。
/// 端点签名契约由 CloudPan.Contract/Generated/ClientApi.g.cs 生成（T-086）；本 partial 仅补充
/// 客户端进程内限速配置入口（T-063），改端点/签名不涉及此处。
/// </summary>
public partial interface IApiClient
{
    /// <summary>运行时更新上传限速（T-063，无需重启客户端）。0 = 不限速。</summary>
    void SetUploadLimit(long bytesPerSecond);

    /// <summary>运行时更新下载限速（T-063，无需重启客户端）。0 = 不限速。</summary>
    void SetDownloadLimit(long bytesPerSecond);
}
