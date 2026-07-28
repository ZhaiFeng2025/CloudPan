namespace CloudPan.Server.Middleware;

/// <summary>
/// 请求关联 ID 中间件——每个请求生成唯一标识，添加到响应头和日志上下文。
/// </summary>
public class RequestIdMiddleware
{
    private readonly RequestDelegate _next;

    public RequestIdMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        var requestId = Guid.NewGuid().ToString("N")[..12];
        context.Response.Headers["X-Request-Id"] = requestId;
        context.Items["RequestId"] = requestId;

        using (Serilog.Context.LogContext.PushProperty("RequestId", requestId))
        {
            await _next(context);
        }
    }
}

public static class RequestIdMiddlewareExtensions
{
    public static IApplicationBuilder UseRequestId(this IApplicationBuilder builder)
    {
        return builder.UseMiddleware<RequestIdMiddleware>();
    }
}
