using System.Collections.Concurrent;
using CloudPan.Contract;

namespace CloudPan.Server.Host.Middleware;

/// <summary>
/// 速率限制中间件——基于可信设备 ID（context.Items["DeviceId"]）或 RemoteIpAddress 兜底。
/// 每设备每分钟最多 SpecConfig.RateLimitPerMinute 次 API 调用。
/// 文件上传/下载和公开端点不计入限制。
/// </summary>
public class RateLimitMiddleware
{
    private readonly RequestDelegate _next;
    private static readonly ConcurrentDictionary<string, RateLimitEntry> Counters = new();
    private static readonly System.Threading.Timer CleanupTimer;

    static RateLimitMiddleware()
    {
        CleanupTimer = new System.Threading.Timer(_ =>
        {
            var now = DateTime.UtcNow;
            foreach (var (key, entry) in Counters)
            {
                if (now - entry.WindowStart > TimeSpan.FromMinutes(1))
                {
                    Counters.TryRemove(key, out var _);
                }
            }
        }, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    public RateLimitMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        string path = context.Request.Path.Value ?? "";

        // 文件上传/下载不计入限制（含分块上传）
        if ((path == "/api/files/upload" || path == "/api/files/upload/chunk") && context.Request.Method == "POST")
        {
            await _next(context);
            return;
        }
        if (path.StartsWith("/api/files/download") && context.Request.Method == "GET")
        {
            await _next(context);
            return;
        }

        // 公开健康检查不计入限制；/share/ 公开端点按 IP 限流（无 DeviceId → RemoteIpAddress），防分享密码暴力破解
        if (path == "/api/health")
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

        var entry = Counters.GetOrAdd(key, _ => new RateLimitEntry { WindowStart = now, Count = 0 });

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
