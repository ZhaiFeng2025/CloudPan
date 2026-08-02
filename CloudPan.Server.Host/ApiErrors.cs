using System.Text.Json;
using CloudPan.Shared;
using Microsoft.AspNetCore.Mvc;

namespace CloudPan.Server;

/// <summary>
/// 统一 API 错误响应工厂。
/// 这是创建错误响应的<b>唯一入口</b>——所有控制器和中间件必须通过此类返回错误。
/// 禁止在代码中手写错误码字符串（如 "BAD_REQUEST"）或匿名错误对象。
/// 违反此规则将由 Roslyn Analyzer CP001/CP002 在编译时报错。
/// </summary>
public static class ApiErrors
{
    /// <summary>400 — 请求参数无效</summary>
    public static ErrorResponse BadRequest(string message, string? friendlyMessage = null, string? detail = null)
        => new(HttpErrorCode.BAD_REQUEST.Code, message, friendlyMessage ?? message, detail);

    /// <summary>400 — 设备 ID 格式无效</summary>
    public static ErrorResponse InvalidDeviceId(string message, string? friendlyMessage = null, string? detail = null)
        => new(HttpErrorCode.INVALID_DEVICE_ID.Code, message, friendlyMessage ?? message, detail);

    /// <summary>401 — 未认证</summary>
    public static ErrorResponse Unauthorized(string message, string? friendlyMessage = null, string? detail = null)
        => new(HttpErrorCode.UNAUTHORIZED.Code, message, friendlyMessage ?? message, detail);

    /// <summary>404 — 资源不存在</summary>
    public static ErrorResponse NotFound(string message, string? friendlyMessage = null, string? detail = null)
        => new(HttpErrorCode.NOT_FOUND.Code, message, friendlyMessage ?? message, detail);

    /// <summary>409 — 版本冲突</summary>
    public static ErrorResponse Conflict(string message, string? friendlyMessage = null, string? detail = null)
        => new(HttpErrorCode.CONFLICT.Code, message, friendlyMessage ?? message, detail);

    /// <summary>413 — 文件过大</summary>
    public static ErrorResponse PayloadTooLarge(string message, string? friendlyMessage = null, string? detail = null)
        => new(HttpErrorCode.PAYLOAD_TOO_LARGE.Code, message, friendlyMessage ?? message, detail);

    /// <summary>429 — 请求频率超限</summary>
    public static ErrorResponse RateLimited(string message, string? friendlyMessage = null, string? detail = null)
        => new(HttpErrorCode.RATE_LIMITED.Code, message, friendlyMessage ?? message, detail);

    /// <summary>500 — 服务端内部错误</summary>
    public static ErrorResponse InternalError(string message, string? friendlyMessage = null, string? detail = null)
        => new(HttpErrorCode.INTERNAL_ERROR.Code, message, friendlyMessage ?? message, detail);

    /// <summary>503 — 服务不可用</summary>
    public static ErrorResponse ServiceUnavailable(string message, string? friendlyMessage = null, string? detail = null)
        => new(HttpErrorCode.SERVICE_UNAVAILABLE.Code, message, friendlyMessage ?? message, detail);
}

/// <summary>
/// 错误响应的 ASP.NET Core 扩展方法。
/// </summary>
public static class ApiErrorExtensions
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    /// <summary>
    /// 从 Controller 返回错误响应（自动使用 HttpErrorCode 中定义的正确 HTTP 状态码）。
    /// </summary>
    public static IActionResult Error(this ControllerBase controller, ErrorCode errorCode, string message,
        string? friendlyMessage = null, string? detail = null)
    {
        ErrorResponse response = new ErrorResponse(errorCode.Code, message, friendlyMessage ?? message, detail);
        return new ObjectResult(response.ToApiBody())
        {
            StatusCode = errorCode.HttpStatus
        };
    }

    /// <summary>
    /// 从中间件写入错误响应到 HttpContext（自动使用正确的 HTTP 状态码和 Content-Type）。
    /// </summary>
    public static async Task WriteErrorAsync(this HttpContext context, ErrorCode errorCode, string message,
        string? friendlyMessage = null, string? detail = null)
    {
        ErrorResponse response = new ErrorResponse(errorCode.Code, message, friendlyMessage ?? message, detail);
        context.Response.StatusCode = errorCode.HttpStatus;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsync(JsonSerializer.Serialize(response.ToApiBody(), JsonOpts));
    }

    /// <summary>
    /// 从中间件写入错误响应——使用已构造的 ErrorResponse。
    /// </summary>
    public static async Task WriteErrorAsync(this HttpContext context, ErrorResponse response, int statusCode)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsync(JsonSerializer.Serialize(response.ToApiBody(), JsonOpts));
    }
}
