using System.Data.Common;
using System.Net.Sockets;
using CloudPan.Shared;

namespace CloudPan.Server.Middleware;

/// <summary>
/// 全局异常处理中间件——捕获所有未处理异常，返回统一 JSON 错误体。
/// 开发环境显示异常详情，生产环境只显示通用错误信息。
/// </summary>
public class GlobalExceptionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IWebHostEnvironment _env;

    /// <summary>友好错误消息映射表。</summary>
    private static readonly Dictionary<Func<Exception, bool>, string> FriendlyMessageMap = new()
    {
        // SQLite 数据库锁定
        [ex => ex is DbException && ex.Message.Contains("database is locked", StringComparison.OrdinalIgnoreCase)
              || ex is InvalidOperationException && ex.Message.Contains("database is locked", StringComparison.OrdinalIgnoreCase)] =
            "服务繁忙，请稍后重试",
        // 连接被拒绝
        [ex => ex is SocketException socketEx && socketEx.Message.Contains("connection refused", StringComparison.OrdinalIgnoreCase)] =
            "无法连接到服务",
        // 操作超时（注意：括号必须——|| 优先级低于 &&，不加括号会将所有 TaskCanceledException 误判为超时）
        [ex => (ex is TaskCanceledException || ex is OperationCanceledException) && ex.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase)] =
            "操作超时，请检查网络",
        // 文件未找到 / 目录未找到
        [ex => ex is FileNotFoundException || ex is DirectoryNotFoundException] =
            "文件访问失败，请检查同步目录",
        // 权限不足
        [ex => ex is UnauthorizedAccessException] =
            "权限不足，请检查目录访问权限",
        // IO 异常（网络断开、管道损坏）
        [ex => ex is IOException && ex is not FileNotFoundException && ex is not DirectoryNotFoundException] =
            "文件读写失败，请检查网络连接",
        // HTTP 请求异常
        [ex => ex is HttpRequestException] =
            "服务端请求失败，请检查服务端是否正常运行",
        // JSON 解析异常
        [ex => ex is System.Text.Json.JsonException] =
            "数据处理异常，请检查数据格式",
    };

    /// <summary>默认友好消息。</summary>
    private const string DefaultFriendlyMessage = "服务暂时不可用";

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
            // 始终将异常写入 Serilog
            var logger = context.RequestServices.GetService<ILogger<GlobalExceptionMiddleware>>();
            logger?.LogError(ex, "未处理异常: {Path}", context.Request.Path);

            // AggregateException 解包：取内层异常匹配
            if (ex is AggregateException aggEx)
            {
                var inner = aggEx.InnerExceptions.FirstOrDefault() ?? aggEx.InnerException;
                if (inner != null)
                {
                    ex = inner;
                }
            }

            // 匹配友好错误消息
            string friendlyMessage = DefaultFriendlyMessage;
            foreach (var (matcher, friendlyMsg) in FriendlyMessageMap)
            {
                if (matcher(ex))
                {
                    friendlyMessage = friendlyMsg;
                    break;
                }
            }

            string detail = _env.IsDevelopment() ? ex.ToString() : $"内部服务器错误: {ex.Message}";
            try
            {
                await context.WriteErrorAsync(HttpErrorCode.INTERNAL_ERROR,
                    friendlyMessage,
                    friendlyMessage,
                    detail);
            }
            catch
            {
                // 客户端已断开或响应头已发送——无法写入错误响应，静默放弃
            }
        }
    }
}

/// <summary>全局异常处理中间件的扩展方法（注册统一错误响应格式）。</summary>
public static class GlobalExceptionMiddlewareExtensions
{
    public static IApplicationBuilder UseGlobalExceptionHandler(this IApplicationBuilder builder)
    {
        return builder.UseMiddleware<GlobalExceptionMiddleware>();
    }
}
