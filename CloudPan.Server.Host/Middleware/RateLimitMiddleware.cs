using CloudPan.Contract;
using Microsoft.Extensions.Caching.Memory;

namespace CloudPan.Server.Host.Middleware;

/// <summary>
/// 速率限制中间件——基于可信设备 ID（context.Items["DeviceId"]）或 RemoteIpAddress 兜底。
/// 每设备每分钟最多 SpecConfig.RateLimitPerMinute 次 API 调用。
/// 文件上传/下载和公开健康检查不计入限制。
/// 状态存于 IMemoryCache（滑动过期自动清理，无裸 Timer，资源生命周期由缓存接管，见 T-048）。
/// </summary>
public class RateLimitMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IMemoryCache _cache;

    public RateLimitMiddleware(RequestDelegate next, IMemoryCache cache)
    {
        _next = next;
        _cache = cache;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        string path = context.Request.Path.Value ?? "";
        string method = context.Request.Method;

        // 文件上传/下载不计入限制（含分块上传）——路由常量与 shared-spec.json 契约同源（T-048，禁魔数字符串）
        if (method == "POST" && (path == SpecRoutes.FilesUpload || path == SpecRoutes.FilesUploadChunk))
        {
            await _next(context);
            return;
        }
        if (method == "GET" && path.StartsWith(SpecRoutes.FilesDownload, StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        // 公开健康检查不计入限制；/share/ 公开端点按 IP 限流（无 DeviceId → RemoteIpAddress），防分享密码暴力破解
        if (path == SpecRoutes.Health)
        {
            await _next(context);
            return;
        }

        // 限流键：优先使用认证后的可信 DeviceId，未认证使用 RemoteIpAddress
        string? deviceId = context.Items["DeviceId"] as string;
        string key = deviceId != null
            ? $"rate:device:{deviceId}"
            : $"rate:ip:{context.Connection.RemoteIpAddress ?? System.Net.IPAddress.None}";
        var now = DateTime.UtcNow;

        // 状态存于 IMemoryCache：滑动过期条目由缓存内部清理（替代原 static Timer，见 T-048）
        var entry = _cache.GetOrCreate(key, cacheEntry =>
        {
            cacheEntry.SlidingExpiration = TimeSpan.FromMinutes(2);
            return new RateLimitEntry { WindowStart = now, Count = 0 };
        })!;

        bool limited = false;
        lock (entry)
        {
            // 滑动窗口：如果超过 1 分钟，重置计数
            if (now - entry.WindowStart > TimeSpan.FromMinutes(1))
            {
                entry.WindowStart = now;
                entry.Count = 0;
            }

            entry.Count++;

            if (entry.Count > SpecConfig.RateLimitPerMinute)
            {
                context.Response.Headers[SpecConfig.RetryAfterHeader] = "60";
                limited = true;
            }
        }

        if (limited)
        {
            await context.WriteErrorAsync(HttpErrorCode.RATE_LIMITED,
                $"请求过于频繁（{SpecConfig.RateLimitPerMinute} 次/分钟），请等待 1 分钟",
                "请求过于频繁，请稍后重试");
            return;
        }

        await _next(context);
    }

    private class RateLimitEntry
    {
        public DateTime WindowStart { get; set; }
        public int Count { get; set; }
    }
}

/// <summary>限流中间件的扩展方法（按 IP 限流，防暴力破解）。</summary>
public static class RateLimitMiddlewareExtensions
{
    public static IApplicationBuilder UseRateLimit(this IApplicationBuilder builder)
    {
        return builder.UseMiddleware<RateLimitMiddleware>();
    }
}
