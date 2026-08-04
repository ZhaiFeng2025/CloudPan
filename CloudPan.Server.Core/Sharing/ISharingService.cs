using CloudPan.Contract;
using CloudPan.Infrastructure.Models;

namespace CloudPan.Server.Core;

/// <summary>创建分享链接结果。</summary>
public sealed record ShareCreateResult(bool Success, string? ShareId, string? ExpiresAt, int? MaxDownloads, DomainError? Error = null);

/// <summary>撤销分享链接结果。</summary>
public sealed record ShareRevokeResult(bool Success, string? ShareId, DomainError? Error = null);

/// <summary>分享访问信息（分享页渲染与校验所需数据）。</summary>
public sealed record ShareInfoResult(
    bool Success,
    Share? Share = null,
    string? FileName = null,
    long FileSize = 0,
    bool Expired = false,
    bool DownloadLimitReached = false,
    bool RequiresPassword = false,
    bool PasswordCorrect = true,
    DomainError? Error = null);

/// <summary>分享下载准备结果。Success 时 Content 为可读取的文件流。</summary>
public sealed record ShareDownloadResult(bool Success, Stream? Content = null, string? FileName = null, DomainError? Error = null);

/// <summary>
/// 文件分享领域服务。封装分享的创建/撤销/访问校验与下载次数原子递增，
/// 使 Controller 只做参数绑定与状态码适配（F-02 下沉载体）。
/// </summary>
public interface ISharingService
{
    /// <summary>创建分享链接。</summary>
    Task<ShareCreateResult> CreateShareAsync(string filePath, string? password, string? expiresAt, int? maxDownloads, string deviceId);

    /// <summary>撤销分享链接。</summary>
    Task<ShareRevokeResult> RevokeShareAsync(string shareId);

    /// <summary>列出当前设备创建的分享链接（不含 token/密码哈希等敏感字段，供管理入口展示）。</summary>
    Task<List<ShareListItem>> ListSharesAsync(string deviceId);

    /// <summary>获取分享访问信息（过期/密码/下载上限校验 + 文件名与大小）。</summary>
    Task<ShareInfoResult> GetShareInfoAsync(string shareId, string? password = null);

    /// <summary>准备分享下载：校验密码与文件存在，原子递增下载计数，打开文件流。</summary>
    Task<ShareDownloadResult> PrepareDownloadAsync(string shareId, string? password);
}
