using System.Net;
using System.Security.Cryptography;
using CloudPan.Server.Data;
using CloudPan.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace CloudPan.Server.Middleware;

/// <summary>
/// Token 认证中间件。
/// 认证模式由端点元数据（EndpointAuthAttribute）驱动，未标注的端点回退到 SpecEndpoints 契约表：
/// AuthMode.Public/Message 跳过 HTTP 头检查（WebSocket 为消息级认证）；AuthMode.Localhost 仅允许回环地址访问。
/// </summary>
public class TokenAuthMiddleware
{
    private readonly RequestDelegate _next;

    public TokenAuthMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // 解析认证模式：优先读端点元数据，非控制器路由（如 /ws）回退 SpecEndpoints 契约表
        AuthMode mode = ResolveAuthMode(context);

        // 公开端点和 WebSocket（消息级认证）跳过 HTTP 头认证
        if (mode is AuthMode.Public or AuthMode.Message)
        {
            await _next(context);
            return;
        }

        // 仅本机端点：检查回环地址（管理面板 /admin、配对页 /pair）
        if (mode == AuthMode.Localhost)
        {
            if (!IsLoopback(context.Connection.RemoteIpAddress))
            {
                await context.WriteErrorAsync(HttpErrorCode.UNAUTHORIZED,
                    "该端点仅允许本机访问",
                    "请通过 127.0.0.1 从本机访问");
                return;
            }

            await _next(context);
            return;
        }

        // 提取 Bearer token
        string? authHeader = context.Request.Headers.Authorization.FirstOrDefault();
        if (string.IsNullOrEmpty(authHeader) || !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            await context.WriteErrorAsync(HttpErrorCode.UNAUTHORIZED,
                "缺少 Authorization: Bearer {token} 头",
                "请提供验证 Token 以连接服务");
            return;
        }

        string token = authHeader["Bearer ".Length..].Trim();
        if (string.IsNullOrEmpty(token))
        {
            await context.WriteErrorAsync(HttpErrorCode.UNAUTHORIZED,
                "Token 为空",
                "请提供有效的家庭共享 Token");
            return;
        }

        // 验证 token 哈希（内存缓存避免每次请求查 DB）
        string tokenHash = ComputeSha256(token);
        var cache = context.RequestServices.GetRequiredService<IMemoryCache>();
        string? storedHash = await cache.GetOrCreateAsync("token_hash_cache", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5);
            var dbFactory = context.RequestServices.GetRequiredService<IDbContextFactory<CloudPanDbContext>>();
            await using var db = await dbFactory.CreateDbContextAsync();
            return await db.AppConfigs
                .Where(c => c.Key == "token_hash")
                .Select(c => c.Value)
                .FirstOrDefaultAsync();
        });

        if (storedHash == null)
        {
            // Token 未配置——拒绝所有认证请求
            await context.WriteErrorAsync(HttpErrorCode.SERVICE_UNAVAILABLE,
                "服务尚未初始化，请稍后重试",
                "服务尚未初始化，请稍后重试");
            return;
        }

        // 使用 Ordinal 比较——十六进制哈希统一 lowercase，无需 IgnoreCase
        if (!string.Equals(tokenHash, storedHash, StringComparison.Ordinal))
        {
            await context.WriteErrorAsync(HttpErrorCode.UNAUTHORIZED,
                "Token 无效",
                "Token 不正确，请确认是否与服务端显示的 Token 一致");
            return;
        }

        // 提取设备 ID（放在 HttpContext.Items 中供控制器使用）
        var dbFactory = context.RequestServices.GetRequiredService<IDbContextFactory<CloudPanDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();
        string? deviceId = context.Request.Headers["X-Device-Id"].FirstOrDefault();
        if (!string.IsNullOrEmpty(deviceId))
        {
            // 校验 Device ID 格式
            if (deviceId.Length > 64 || !System.Text.RegularExpressions.Regex.IsMatch(deviceId, @"^[a-zA-Z0-9_-]+$"))
            {
                await context.WriteErrorAsync(HttpErrorCode.INVALID_DEVICE_ID,
                    "Device ID 格式无效：长度 1-64，仅允许字母、数字、下划线和短横",
                    "设备标识格式不正确，请检查客户端配置");
                return;
            }

            context.Items["DeviceId"] = deviceId;

            // 自动注册未知设备 + 更新 LastSeen（Online 状态由 WebSocket 管理）
            var device = await db.Devices.FindAsync(deviceId);
            if (device == null)
            {
                db.Devices.Add(new Models.Device
                {
                    Id = deviceId,
                    Name = $"设备-{deviceId[..Math.Min(8, deviceId.Length)]}",
                    Person = null,
                    LastSeen = DateTime.UtcNow.ToString("O"),
                    Online = 0, // HTTP 请求不表示实时在线（WebSocket 管理）
                    RegisteredAt = DateTime.UtcNow.ToString("O")
                });
            }
            else
            {
                device.LastSeen = DateTime.UtcNow.ToString("O");
                // Online 由 WebSocket 连接/断开管理，不在 HTTP 请求中更新
            }
            try
            {
                await db.SaveChangesAsync();
            }
            catch (DbUpdateException ex)
            {
                // 仅唯一约束冲突（并发竞态：另一请求已注册该设备）可重试；
                // 其他约束违反（外键、非空等）不可重试，直接抛出。
                if (!IsUniqueConstraintViolation(ex))
                {
                    throw;
                }

                // 并发竞态：另一请求已先行注册该设备。
                // 关键：当前 db 仍跟踪 Add 失败的实体（状态=Added），FindAsync 会优先返回
                // 变更追踪器中的该失败实体而非数据库中的真值，导致二次 INSERT 冲突。
                // 必须使用全新的 DbContext 执行重试查询。
                var logger = context.RequestServices
                    .GetRequiredService<ILogger<TokenAuthMiddleware>>();
                logger.LogWarning("设备 {DeviceId} 注册并发冲突（正常竞态条件），使用新 DbContext 查询", deviceId);
                await using var freshDb = await dbFactory.CreateDbContextAsync();
                var freshDevice = await freshDb.Devices.FindAsync(deviceId);
                if (freshDevice != null)
                {
                    freshDevice.LastSeen = DateTime.UtcNow.ToString("O");
                    await freshDb.SaveChangesAsync();
                }
            }
        }

        await _next(context);
    }

    /// <summary>判断 DbUpdateException 是否由唯一约束/主键冲突触发（可重试），而非外键/非空等不可重试约束。</summary>
    private static bool IsUniqueConstraintViolation(DbUpdateException ex)
    {
        // SQLite 错误码 19 = SQLITE_CONSTRAINT（含 UNIQUE 和 PRIMARY KEY）
        // 在 Microsoft.Data.Sqlite 中内部异常包含 "UNIQUE constraint failed" 或 SQLite 错误码 19
        var inner = ex.InnerException;
        while (inner != null)
        {
            string msg = inner.Message;
            if (msg.Contains("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("SQLITE_CONSTRAINT", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("constraint failed", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            inner = inner.InnerException;
        }
        return false;
    }

    /// <summary>
    /// 解析端点认证模式。
    /// 1. 控制器路由：读取 EndpointAuthAttribute（类级别默认值）。
    /// 2. 非控制器路由（如 /ws）或未标注控制器：按方法+路径查 SpecEndpoints 契约表；
    ///    契约中未注册的路径安全默认要求 Token 认证。
    /// </summary>
    private static AuthMode ResolveAuthMode(HttpContext context)
    {
        var attribute = context.GetEndpoint()?.Metadata.GetMetadata<EndpointAuthAttribute>();
        if (attribute != null)
        {
            return attribute.Mode;
        }

        return FindEndpointSpec(context)?.Auth ?? AuthMode.Token;
    }

    /// <summary>按 HTTP 方法 + 路径查 SpecEndpoints（模板参数路径如 /share/{shareId} 归一化匹配）。</summary>
    private static EndpointSpec? FindEndpointSpec(HttpContext context)
    {
        string method = context.Request.Method;
        string path = context.Request.Path.Value ?? "/";

        var ep = SpecEndpoints.Find(method, path);
        if (ep != null)
        {
            return ep;
        }

        // 模板参数归一化：/share/abc123 → /share/{x}
        int lastSlash = path.LastIndexOf('/');
        if (lastSlash > 0)
        {
            ep = SpecEndpoints.Find(method, path[..lastSlash] + "/{x}");
        }

        return ep;
    }

    /// <summary>检查地址是否为回环地址（含 IPv4-mapped IPv6，如 ::ffff:127.0.0.1）。</summary>
    private static bool IsLoopback(IPAddress? ip)
    {
        if (ip == null)
        {
            return false;
        }

        return IPAddress.IsLoopback(ip)
            || (ip.IsIPv4MappedToIPv6 && IPAddress.IsLoopback(ip.MapToIPv4()));
    }

    /// <summary>计算 SHA-256（64 字符十六进制）。</summary>
    private static string ComputeSha256(string input)
    {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(input);
        byte[] hash = SHA256.HashData(bytes);
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
