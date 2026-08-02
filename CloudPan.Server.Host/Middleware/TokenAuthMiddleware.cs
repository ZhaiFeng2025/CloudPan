using System.Net;
using CloudPan.Contract;
using CloudPan.Server.Core;

namespace CloudPan.Server.Host.Middleware;

/// <summary>
/// Token 认证中间件。
/// 认证模式由端点元数据（EndpointAuthAttribute）驱动，未标注的端点回退到 SpecEndpoints 契约表：
/// AuthMode.Public/Message 跳过 HTTP 头检查（WebSocket 为消息级认证）；AuthMode.Localhost 仅允许回环地址访问。
/// Token 校验与设备注册收敛到 ITokenService（F-25/T-025 单一事实来源），本中间件只做 HTTP 头解析与适配，
/// 不再直碰 DbContext/内存缓存/设备实体。
/// </summary>
public class TokenAuthMiddleware
{
    private readonly RequestDelegate _next;

    public TokenAuthMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, ITokenService tokenService)
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

        // 验证 Token（经 ITokenService 单一实现：SHA-256 比对 + 5 分钟内存缓存，与 WebSocket 认证一致）
        TokenValidationResult validation = await tokenService.ValidateTokenAsync(token);
        if (validation == TokenValidationResult.NotInitialized)
        {
            // Token 未配置——拒绝所有认证请求
            await context.WriteErrorAsync(HttpErrorCode.SERVICE_UNAVAILABLE,
                "服务尚未初始化，请稍后重试",
                "服务尚未初始化，请稍后重试");
            return;
        }

        if (validation == TokenValidationResult.Invalid)
        {
            await context.WriteErrorAsync(HttpErrorCode.UNAUTHORIZED,
                "Token 无效",
                "Token 不正确，请确认是否与服务端显示的 Token 一致");
            return;
        }

        // 提取设备 ID（放在 HttpContext.Items 中供控制器使用）；格式校验/自动注册/LastSeen 收敛在 ITokenService
        string? deviceId = context.Request.Headers["X-Device-Id"].FirstOrDefault();
        if (!string.IsNullOrEmpty(deviceId))
        {
            if (!await tokenService.EnsureDeviceAsync(deviceId))
            {
                await context.WriteErrorAsync(HttpErrorCode.INVALID_DEVICE_ID,
                    "Device ID 格式无效：长度 1-64，仅允许字母、数字、下划线和短横",
                    "设备标识格式不正确，请检查客户端配置");
                return;
            }

            context.Items["DeviceId"] = deviceId;
        }

        await _next(context);
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
}

/// <summary>TokenAuthMiddleware 扩展方法。</summary>
public static class TokenAuthMiddlewareExtensions
{
    public static IApplicationBuilder UseTokenAuth(this IApplicationBuilder builder)
    {
        return builder.UseMiddleware<TokenAuthMiddleware>();
    }
}
