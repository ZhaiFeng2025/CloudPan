using CloudPan.Contract;
using CloudPan.Server.Core;
using CloudPan.Server.Host;
using Microsoft.AspNetCore.Mvc;

namespace CloudPan.Server.Host.Controllers;

/// <summary>
/// 文件分享控制器——只做参数绑定与状态码适配，领域逻辑（分享 CRUD/校验/下载计数递增）在 Server.Core ISharingService。
/// /api/shares 需要 Token 认证；/share/{id} 公开访问（手机浏览器可直接打开）。
/// </summary>
[ApiController]
[EndpointAuth(AuthMode.Token)]
public class ShareController : ControllerBase
{
    private readonly ISharingService _sharing;

    public ShareController(ISharingService sharing)
    {
        _sharing = sharing;
    }

    /// <summary>
    /// POST /api/shares — 创建分享链接。
    /// </summary>
    [HttpPost(SpecRoutes.Shares)]
    public async Task<IActionResult> CreateShare([FromBody] CreateShareRequestDto request)
    {
        if (string.IsNullOrWhiteSpace(request.FilePath))
        {
            return this.Error(HttpErrorCode.BAD_REQUEST, "filePath 不能为空", "文件路径不能为空");
        }

        string deviceId = HttpContext.Items["DeviceId"] as string ?? "unknown";
        var result = await _sharing.CreateShareAsync(
            request.FilePath, request.Password, request.ExpiresAt, request.MaxDownloads, deviceId);
        if (!result.Success)
        {
            return this.Error(result.Error!.Code, result.Error.Message, result.Error.UserMessage, result.Error.Detail);
        }

        string baseUrl = $"{Request.Scheme}://{Request.Host}";
        return Ok(new ShareCreateResponse(
            new ShareCreateData(result.ShareId!, $"{baseUrl}/share/{result.ShareId}", result.ExpiresAt, result.MaxDownloads)));
    }

    /// <summary>
    /// GET /api/shares — 分享链接列表（当前设备创建，不含 token 等敏感字段）。
    /// </summary>
    [HttpGet(SpecRoutes.SharesGet)]
    public async Task<IActionResult> ListShares()
    {
        string deviceId = HttpContext.Items["DeviceId"] as string ?? "unknown";
        var shares = await _sharing.ListSharesAsync(deviceId);
        return Ok(new ShareListResponse(shares.ToArray()));
    }

    /// <summary>
    /// DELETE /api/shares/{shareId} — 撤销分享链接。
    /// </summary>
    [HttpDelete(SpecRoutes.SharesByShareId)]
    public async Task<IActionResult> RevokeShare(string shareId)
    {
        var result = await _sharing.RevokeShareAsync(shareId);
        if (!result.Success)
        {
            return this.Error(result.Error!.Code, result.Error.Message, result.Error.UserMessage);
        }

        return Ok(new ShareRevokeResponse(new ShareRevokeData(result.ShareId!)));
    }

    /// <summary>
    /// GET /share/{shareId} — 分享页面（HTML，手机浏览器友好）。
    /// </summary>
    [HttpGet(SpecRoutes.ShareByShareId)]
    [EndpointAuth(AuthMode.Public)]
    public async Task<IActionResult> SharePage(string shareId, [FromQuery] string? password = null)
    {
        var info = await _sharing.GetShareInfoAsync(shareId, password);
        if (!info.Success)
        {
            return this.Error(info.Error!.Code, info.Error.Message, info.Error.UserMessage);
        }

        // 检查过期
        if (info.Expired)
        {
            return this.Error(HttpErrorCode.BAD_REQUEST, "分享链接已过期", "分享链接已过期");
        }

        // 检查密码
        if (info.RequiresPassword && !info.PasswordCorrect)
        {
            if (string.IsNullOrEmpty(password))
            {
                return Content(PasswordFormHtml, "text/html; charset=utf-8");
            }

            return Content(PasswordErrorHtml, "text/html; charset=utf-8");
        }

        // 检查下载次数
        if (info.DownloadLimitReached)
        {
            return this.Error(HttpErrorCode.BAD_REQUEST, "下载次数已用完", "下载次数已用完，无法继续下载");
        }

        string fileName = info.FileName ?? "";
        long fileSize = info.FileSize;
        string sizeStr = fileSize > 1_048_576
            ? $"{fileSize / 1_048_576.0:F1} MB"
            : $"{fileSize / 1024.0:F0} KB";

        return Content(
            $"<html><head><meta name='viewport' content='width=device-width,initial-scale=1'></head>" +
            $"<body style='font-family:sans-serif;padding:2em;text-align:center'>" +
            $"<h2>📁 {System.Net.WebUtility.HtmlEncode(fileName)}</h2>" +
            $"<p>{sizeStr}</p>" +
            $"<a href='/share/{shareId}/download{(password != null ? $"?password={Uri.EscapeDataString(password)}" : "")}' " +
            $"style='display:inline-block;padding:12px 32px;background:#0078d4;color:white;" +
            $"border-radius:6px;text-decoration:none;font-size:18px'>⬇ 下载文件</a>" +
            $"<p style='margin-top:2em;color:#888;font-size:12px'>CloudPan 文件分享</p>" +
            $"</body></html>",
            "text/html; charset=utf-8");
    }

    /// <summary>
    /// GET /share/{shareId}/download — 下载分享文件。
    /// </summary>
    [HttpGet(SpecRoutes.ShareByShareIdDownload)]
    [EndpointAuth(AuthMode.Public)]
    public async Task<IActionResult> ShareDownload(string shareId, [FromQuery] string? password = null)
    {
        var result = await _sharing.PrepareDownloadAsync(shareId, password);
        if (!result.Success)
        {
            return this.Error(result.Error!.Code, result.Error.Message, result.Error.UserMessage);
        }

        return File(result.Content!, "application/octet-stream", result.FileName);
    }

    private const string PasswordFormHtml =
        "<html><body style='font-family:sans-serif;padding:2em;text-align:center'>" +
        "<h2>请输入访问密码</h2>" +
        "<form method='get'><input name='password' type='password' placeholder='密码'/>" +
        "<button type='submit'>确认</button></form></body></html>";

    private const string PasswordErrorHtml =
        "<html><body style='font-family:sans-serif;padding:2em;text-align:center'>" +
        "<h2 style='color:red'>密码错误</h2>" +
        "<a href='javascript:history.back()'>返回重试</a></body></html>";
}
