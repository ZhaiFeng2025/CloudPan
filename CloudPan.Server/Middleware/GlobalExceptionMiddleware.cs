using System.Text.Json;

namespace CloudPan.Server.Middleware;

/// <summary>
/// 全局异常处理中间件——捕获所有未处理异常，返回统一 JSON 错误体。
/// 开发环境显示异常详情，生产环境只显示通用错误信息。
/// </summary>
public class GlobalExceptionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IWebHostEnvironment _env;

    public GlobalExceptionMiddleware(RequestDelegate next, IWebHostEnvironment env)
    {
        _next = next;
        _env = env;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            context.Response.StatusCode = 500;
            context.Response.ContentType = "application/json";

            var message = _env.IsDevelopment()
                ? $"内部服务器错误: {ex.Message}"
                : "内部服务器错误";

            var errorBody = new Dictionary<string, object>
            {
                ["error"] = new Dictionary<string, object>
                {
                    ["code"] = "INTERNAL_ERROR",
                    ["message"] = message
                }
            };
            if (_env.IsDevelopment())
                ((Dictionary<string, object>)errorBody["error"])["detail"] = ex.ToString();

            await context.Response.WriteAsync(JsonSerializer.Serialize(errorBody));
        }
    }
}

public static class GlobalExceptionMiddlewareExtensions
{
    public static IApplicationBuilder UseGlobalExceptionHandler(this IApplicationBuilder builder)
    {
        return builder.UseMiddleware<GlobalExceptionMiddleware>();
    }
}
