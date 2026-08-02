using System.Globalization;
using System.Security.Cryptography;
using CloudPan.Server.Data;
using CloudPan.Server.Models;
using CloudPan.Shared;
using Microsoft.EntityFrameworkCore;

namespace CloudPan.Server.Services;

/// <inheritdoc />
public class SharingService : ISharingService
{
    private readonly IDbContextFactory<CloudPanDbContext> _dbFactory;
    private readonly IFileStorageService _storage;
    private readonly IFileIndexService _index;

    public SharingService(
        IDbContextFactory<CloudPanDbContext> dbFactory,
        IFileStorageService storage,
        IFileIndexService index)
    {
        _dbFactory = dbFactory;
        _storage = storage;
        _index = index;
    }

    /// <inheritdoc />
    public async Task<ShareCreateResult> CreateShareAsync(
        string filePath, string? password, string? expiresAt, int? maxDownloads, string deviceId)
    {
        // 路径安全统一防线：任何"路径 → 绝对路径"转换前先经 ValidatePath
        string? pathErr = _storage.ValidatePath(filePath);
        if (pathErr != null)
        {
            return new ShareCreateResult(false, null, null, null,
                new DomainError(HttpErrorCode.BAD_REQUEST, pathErr, "文件路径不能为空"));
        }

        var entry = await _index.GetByPathAsync(filePath);
        if (entry == null)
        {
            return new ShareCreateResult(false, null, null, null,
                new DomainError(HttpErrorCode.NOT_FOUND, $"文件不存在: {filePath}", "文件不存在，无法创建分享链接"));
        }

        await using var db = await _dbFactory.CreateDbContextAsync();
        Share share = new Share
        {
            Id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(), // 32 hex
            FilePath = filePath,
            PasswordHash = string.IsNullOrEmpty(password)
                ? null : SharePasswordHasher.Hash(password),
            ExpiresAt = expiresAt,
            MaxDownloads = maxDownloads,
            UsedDownloads = 0,
            CreatedAt = DateTime.UtcNow.ToString("O"),
            CreatedBy = deviceId
        };
        db.Shares.Add(share);
        await db.SaveChangesAsync();

        return new ShareCreateResult(true, share.Id, share.ExpiresAt, share.MaxDownloads);
    }

    /// <inheritdoc />
    public async Task<ShareRevokeResult> RevokeShareAsync(string shareId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var share = await db.Shares.FindAsync(shareId);
        if (share == null)
        {
            return new ShareRevokeResult(false, null,
                new DomainError(HttpErrorCode.NOT_FOUND, "分享链接不存在", "分享链接不存在或已失效"));
        }

        db.Shares.Remove(share);
        await db.SaveChangesAsync();

        return new ShareRevokeResult(true, shareId);
    }

    /// <inheritdoc />
    public async Task<ShareInfoResult> GetShareInfoAsync(string shareId, string? password = null)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var share = await db.Shares.FindAsync(shareId);
        if (share == null)
        {
            return new ShareInfoResult(false,
                Error: new DomainError(HttpErrorCode.NOT_FOUND, "分享链接不存在或已失效", "分享链接不存在或已失效"));
        }

        // 过期校验（ISO 8601 "Z" 后缀解析为 Utc，避免本地时区偏移导致比较错误）
        bool expired = !string.IsNullOrEmpty(share.ExpiresAt)
            && DateTime.TryParse(share.ExpiresAt, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal, out var expires)
            && expires < DateTime.UtcNow;

        bool requiresPassword = !string.IsNullOrEmpty(share.PasswordHash);
        bool passwordCorrect = true;
        if (requiresPassword)
        {
            if (string.IsNullOrEmpty(password))
            {
                passwordCorrect = false; // 未提供密码，由页面引导输入
            }
            else if (!SharePasswordHasher.Verify(password, share.PasswordHash!))
            {
                passwordCorrect = false;
            }
        }

        // 下载上限校验
        bool limitReached = share.MaxDownloads.HasValue && share.UsedDownloads >= share.MaxDownloads.Value;

        string fileName = Path.GetFileName(share.FilePath);
        long fileSize = _storage.Exists(share.FilePath)
            ? _storage.GetSize(share.FilePath) : 0;

        return new ShareInfoResult(
            true, share, fileName, fileSize, expired, limitReached, requiresPassword, passwordCorrect);
    }

    /// <inheritdoc />
    public async Task<ShareDownloadResult> PrepareDownloadAsync(string shareId, string? password)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var share = await db.Shares.FindAsync(shareId);
        if (share == null)
        {
            return new ShareDownloadResult(false,
                Error: new DomainError(HttpErrorCode.NOT_FOUND, "分享链接不存在", "分享链接不存在或已失效"));
        }

        // 密码校验
        if (!string.IsNullOrEmpty(share.PasswordHash))
        {
            if (string.IsNullOrEmpty(password))
            {
                return new ShareDownloadResult(false,
                    Error: new DomainError(HttpErrorCode.UNAUTHORIZED, "需要密码", "该分享设置了访问密码，请输入密码后重试"));
            }

            if (!SharePasswordHasher.Verify(password, share.PasswordHash))
            {
                return new ShareDownloadResult(false,
                    Error: new DomainError(HttpErrorCode.UNAUTHORIZED, "密码错误", "访问密码错误，请重新输入"));
            }
        }

        if (!_storage.Exists(share.FilePath))
        {
            return new ShareDownloadResult(false,
                Error: new DomainError(HttpErrorCode.NOT_FOUND, "文件已被删除", "分享的文件已被删除，无法下载"));
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
            return new ShareDownloadResult(false,
                Error: new DomainError(HttpErrorCode.BAD_REQUEST, "下载次数已用完", "下载次数已用完，无法继续下载"));
        }

        var stream = _storage.OpenRead(share.FilePath);
        string fileName = Path.GetFileName(share.FilePath);
        return new ShareDownloadResult(true, stream, fileName);
    }
}
