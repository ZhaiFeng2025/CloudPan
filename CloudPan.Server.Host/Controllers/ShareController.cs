using System.Security.Cryptography;
using CloudPan.Server;
using CloudPan.Server.Data;
using CloudPan.Server.Models;
using CloudPan.Server.Services;
using CloudPan.Shared;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CloudPan.Server.Controllers;

/// <summary>
/// 文件分享控制器。
/// /api/shares 需要 Token 认证；/share/{id} 公开访问（手机浏览器可直接打开）。
/// </summary>
[ApiController]
[EndpointAuth(AuthMode.Token)]
public class ShareController : ControllerBase
{
    private readonly IDbContextFactory<CloudPanDbContext> _dbFactory;
    private readonly IFileStorageService _storage;
    private readonly IFileIndexService _index;

    public ShareController(
        IDbContextFactory<CloudPanDbContext> dbFactory,
        IFileStorageService storage,
        IFileIndexService index)
    {
        _dbFactory = dbFactory;
        _storage = storage;
        _index = index;
    }

    /// <summary>
    /// POST /api/shares — 创建分享链接。
    /// </summary>
    [HttpPost("/api/shares")]
    public async Task<IActionResult> CreateShare([FromBody] CreateShareRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.FilePath))
        {
            return this.Error(HttpErrorCode.BAD_REQUEST, "filePath 不能为空", "文件路径不能为空");
        }

        var entry = await _index.GetByPathAsync(request.FilePath);
        if (entry == null)
        {
            return this.Error(HttpErrorCode.NOT_FOUND, $"文件不存在: {request.FilePath}", "文件不存在，无法创建分享链接");
        }

        string deviceId = HttpContext.Items["DeviceId"] as string ?? "unknown";

        await using var db = await _dbFactory.CreateDbContextAsync();
        Share share = new Share
        {
            Id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(), // 32 hex
            FilePath = request.FilePath,
            PasswordHash = string.IsNullOrEmpty(request.Password)
                ? null : SharePasswordHasher.Hash(request.Password),
            ExpiresAt = request.ExpiresAt,
            MaxDownloads = request.MaxDownloads,
            UsedDownloads = 0,
            CreatedAt = DateTime.UtcNow.ToString("O"),
            CreatedBy = deviceId
        };
        db.Shares.Add(share);
        await db.SaveChangesAsync();

        string baseUrl = $"{Request.Scheme}://{Request.Host}";
        return Ok(new
        {
            data = new
            {
                shareId = share.Id,
                url = $"{baseUrl}/share/{share.Id}",
                expiresAt = share.ExpiresAt,
                maxDownloads = share.MaxDownloads
            }
        });
    }

    /// <summary>
    /// DELETE /api/shares/{shareId} — 撤销分享链接。
    /// </summary>
    [HttpDelete("/api/shares/{shareId}")]
    public async Task<IActionResult> RevokeShare(string shareId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var share = await db.Shares.FindAsync(shareId);
        if (share == null)
        {
            return this.Error(HttpErrorCode.NOT_FOUND, "分享链接不存在", "分享链接不存在或已失效");
        }

        db.Shares.Remove(share);
        await db.SaveChangesAsync();

        return Ok(new { data = new { revoked = shareId } });
    }

    /// <summary>
    /// GET /share/{shareId} — 分享页面（HTML，手机浏览器友好）。
    /// </summary>
    [HttpGet("/share/{shareId}")]
    [EndpointAuth(AuthMode.Public)]
    public async Task<IActionResult> SharePage(string shareId, [FromQuery] string? password = null)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var share = await db.Shares.FindAsync(shareId);
        if (share == null)
        {
            return this.Error(HttpErrorCode.NOT_FOUND, "分享链接不存在或已失效", "分享链接不存在或已失效");
        }

        // 检查过期
        if (!string.IsNullOrEmpty(share.ExpiresAt)
            && DateTime.TryParse(share.ExpiresAt, out var expires)
            && expires < DateTime.UtcNow)
        {
            return this.Error(HttpErrorCode.BAD_REQUEST, "分享链接已过期", "分享链接已过期");
        }

        // 检查密码
        if (!string.IsNullOrEmpty(share.PasswordHash))
        {
            if (string.IsNullOrEmpty(password))
            {
                return Content(
                    "<html><body style='font-family:sans-serif;padding:2em;text-align:center'>" +
                    "<h2>请输入访问密码</h2>" +
                    "<form method='get'><input name='password' type='password' placeholder='密码'/>" +
                    "<button type='submit'>确认</button></form></body></html>",
                    "text/html; charset=utf-8");
            }

            if (!SharePasswordHasher.Verify(password, share.PasswordHash))
            {
                return Content(
                    "<html><body style='font-family:sans-serif;padding:2em;text-align:center'>" +
                    "<h2 style='color:red'>密码错误</h2>" +
                    "<a href='javascript:history.back()'>返回重试</a></body></html>",
                    "text/html; charset=utf-8");
            }
        }

        // 检查下载次数
        if (share.MaxDownloads.HasValue && share.UsedDownloads >= share.MaxDownloads.Value)
        {
            return this.Error(HttpErrorCode.BAD_REQUEST, "下载次数已用完", "下载次数已用完，无法继续下载");
        }

        string fileName = Path.GetFileName(share.FilePath);
        long fileSize = _storage.Exists(share.FilePath)
            ? _storage.GetSize(share.FilePath) : 0;
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
    [HttpGet("/share/{shareId}/download")]
    [EndpointAuth(AuthMode.Public)]
    public async Task<IActionResult> ShareDownload(string shareId, [FromQuery] string? password = null)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var share = await db.Shares.FindAsync(shareId);
        if (share == null)
        {
            return this.Error(HttpErrorCode.NOT_FOUND, "分享链接不存在", "分享链接不存在或已失效");
        }

        // 密码校验
        if (!string.IsNullOrEmpty(share.PasswordHash))
        {
            if (string.IsNullOrEmpty(password))
            {
                return this.Error(HttpErrorCode.UNAUTHORIZED, "需要密码", "该分享设置了访问密码，请输入密码后重试");
            }

            if (!SharePasswordHasher.Verify(password, share.PasswordHash))
            {
                return this.Error(HttpErrorCode.UNAUTHORIZED, "密码错误", "访问密码错误，请重新输入");
            }
        }

        if (!_storage.Exists(share.FilePath))
        {
            return this.Error(HttpErrorCode.NOT_FOUND, "文件已被删除", "分享的文件已被删除，无法下载");
        }

        // 原子递增下载计数（条件 UPDATE：并发下防止突破 MaxDownloads 上限）。表名为单数 Share（契约 [Table("Share")]）
        int updated = share.MaxDownloads.HasValue
            ? await db.Database.ExecuteSqlRawAsync(
                "UPDATE Share SET UsedDownloads = UsedDownloads + 1 WHERE Id = {0} AND UsedDownloads < {1}",
                shareId, share.MaxDownloads.Value)
            : await db.Database.ExecuteSqlRawAsync(
                "UPDATE Share SET UsedDownloads = UsedDownloads + 1 WHERE Id = {0}", shareId);
        if (updated == 0)
        {
            return this.Error(HttpErrorCode.BAD_REQUEST, "下载次数已用完", "下载次数已用完，无法继续下载");
        }

        var stream = _storage.OpenRead(share.FilePath);
        string fileName = Path.GetFileName(share.FilePath);
        return File(stream, "application/octet-stream", fileName);
    }
}

/// <summary>创建分享请求。</summary>
public record CreateShareRequest(
    string FilePath,
    string? Password = null,
    string? ExpiresAt = null,
    int? MaxDownloads = null
);
