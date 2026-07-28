using System.Collections.Concurrent;

namespace CloudPan.Server.Middleware;

/// <summary>
/// 速率限制中间件——基于 X-Device-Id 头，每设备每分钟最多 60 次 API 调用。
/// 文件上传(POST /api/files/upload)和下载(GET /api/files/download)不计入限制。
/// 使用内存 ConcurrentDictionary 存储计数，定时清理过期条目。
/// </summary>
public class RateLimitMiddleware
{
    private readonly RequestDelegate _next;
    private const int MaxRequestsPerMinute = 60;
    private static readonly ConcurrentDictionary<string, RateLimitEntry> Counters = new();
    private static readonly Timer CleanupTimer;

    static RateLimitMiddleware()
    {
        // 每分钟清理一次过期计数器
        CleanupTimer = new Timer(_ =>
        {
            var now = DateTime.UtcNow;
            foreach (var (key, entry) in Counters)
            {
                if (now - entry.WindowStart > TimeSpan.FromMinutes(1))
                    Counters.TryRemove(key, out var _);
            }
        }, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    public RateLimitMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "";

        // 文件上传/下载不计入限制
        if (path == "/api/files/upload" && context.Request.Method == "POST")
        {
            await _next(context);
            return;
        }
        if (path.StartsWith("/api/files/download") && context.Request.Method == "GET")
        {
            await _next(context);
            return;
        }

        // 公开端点不计入限制
        if (path == "/api/health" || path.StartsWith("/share/"))
        {
            await _next(context);
            return;
        }

        var deviceId = context.Request.Headers["X-Device-Id"].FirstOrDefault() ?? "unknown";
        var key = $"rate:{deviceId}";
        var now = DateTime.UtcNow;

        var entry = Counters.GetOrAdd(key, _ => new RateLimitEntry { WindowStart = now, Count = 0 });

        lock (entry)
        {
            // 滑动窗口：如果超过 1 分钟，重置计数
            if (now - entry.WindowStart > TimeSpan.FromMinutes(1))
            {
                entry.WindowStart = now;
                entry.Count = 0;
            }

            entry.Count++;

            if (entry.Count > MaxRequestsPerMinute)
            {
                context.Response.StatusCode = 429;
                context.Response.Headers["Retry-After"] = "60";
                context.Response.ContentType = "application/json";
                context.Response.WriteAsync(
                    """{"error":{"code":"RATE_LIMITED","message":"请求过于频繁，请等待 1 分钟","retryAfter":60}}""");
                return;
            }
        }

        await _next(context);
    }

    private class RateLimitEntry
    {
        public DateTime WindowStart { get; set; }
        public int Count { get; set; }
    }
}

public static class RateLimitMiddlewareExtensions
{
    public static IApplicationBuilder UseRateLimit(this IApplicationBuilder builder)
    {
        return builder.UseMiddleware<RateLimitMiddleware>();
    }
}
