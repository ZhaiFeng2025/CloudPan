using System.Security.Cryptography;
using CloudPan.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace CloudPan.Server.Middleware;

/// <summary>
/// Token 认证中间件。
/// 验证 Authorization: Bearer {token} 头，比对 AppConfig 中存储的 SHA-256(token)。
/// 无需认证的端点：/api/health、/share/ 下的公开分享链接。
/// </summary>
public class TokenAuthMiddleware
{
    private readonly RequestDelegate _next;

    /// <summary>无需 Token 的路径前缀（公开端点）。</summary>
    private static readonly string[] PublicPaths = ["/api/health", "/share/"];

    public TokenAuthMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // 公开端点跳过认证
        var path = context.Request.Path.Value ?? "";
        if (PublicPaths.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
        {
            await _next(context);
            return;
        }

        // 提取 Bearer token
        var authHeader = context.Request.Headers.Authorization.FirstOrDefault();
        if (string.IsNullOrEmpty(authHeader) || !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = 401;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(
                """{"error":{"code":"UNAUTHORIZED","message":"缺少 Authorization: Bearer {token} 头"}}""");
            return;
        }

        var token = authHeader["Bearer ".Length..].Trim();
        if (string.IsNullOrEmpty(token))
        {
            context.Response.StatusCode = 401;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(
                """{"error":{"code":"UNAUTHORIZED","message":"Token 为空"}}""");
            return;
        }

        // 验证 token 哈希
        var tokenHash = ComputeSha256(token);
        var dbFactory = context.RequestServices.GetRequiredService<IDbContextFactory<CloudPanDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();

        var storedHash = await db.AppConfigs
            .Where(c => c.Key == "token_hash")
            .Select(c => c.Value)
            .FirstOrDefaultAsync();

        if (storedHash == null)
        {
            // Token 尚未初始化——Phase 0 放行（允许首次设置）
            await _next(context);
            return;
        }

        if (!string.Equals(tokenHash, storedHash, StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = 401;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(
                """{"error":{"code":"UNAUTHORIZED","message":"Token 无效"}}""");
            return;
        }

        // 提取设备 ID（放在 HttpContext.Items 中供控制器使用）
        var deviceId = context.Request.Headers["X-Device-Id"].FirstOrDefault();
        if (!string.IsNullOrEmpty(deviceId))
        {
            context.Items["DeviceId"] = deviceId;

            // 自动注册未知设备 + 更新在线状态
            var device = await db.Devices.FindAsync(deviceId);
            if (device == null)
            {
                db.Devices.Add(new Models.Device
                {
                    Id = deviceId,
                    Name = $"设备-{deviceId[..8]}",
                    Person = null,
                    LastSeen = DateTime.UtcNow.ToString("O"),
                    Online = 1,
                    RegisteredAt = DateTime.UtcNow.ToString("O")
                });
            }
            else
            {
                device.LastSeen = DateTime.UtcNow.ToString("O");
                device.Online = 1;
            }
            await db.SaveChangesAsync();
        }

        await _next(context);
    }

    /// <summary>计算 SHA-256（64 字符十六进制）。</summary>
    private static string ComputeSha256(string input)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(input);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

/// <summary>TokenAuthMiddleware 扩展方法。</summary>
public static class TokenAuthMiddlewareExtensions
{
    public static IApplicationBuilder UseTokenAuth(this IApplicationBuilder builder)
    {
        return builder.UseMiddleware<TokenAuthMiddleware>();
    }
}
