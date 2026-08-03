using System.Text.Json;
using CloudPan.Contract;
using CloudPan.Server.Core;
using CloudPan.Server.Host.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CloudPan.Tests.Server.Services;

/// <summary>
/// TokenAuthMiddleware 单元测试——直接构造 HttpContext 调用中间件，
/// 验证 401/503/400 响应与 Token/设备校验接线。T-025 起 Token 校验与设备注册收敛到 ITokenService，
/// 本测试用 FakeTokenService 注入，聚焦中间件的 HTTP 头解析与适配行为（领域逻辑由 TokenServiceTests 覆盖）。
/// </summary>
public class TokenAuthMiddlewareTests : Infrastructure.TestBase
{
    // ============================================================
    // 无 Authorization 头
    // ============================================================

    [Fact]
    public async Task 无Authorization头_返回401()
    {
        bool nextCalled = false;
        var middleware = new TokenAuthMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });
        var context = CreateContext("/api/files/tree");

        await middleware.InvokeAsync(context, new FakeTokenService());

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.False(nextCalled);
        using var body = await ReadResponseBody(context);
        Assert.Equal(HttpErrorCode.UNAUTHORIZED.Code,
            body.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task 非Bearer格式头_返回401()
    {
        var middleware = new TokenAuthMiddleware(_ => Task.CompletedTask);
        var context = CreateContext("/api/files/tree", "Basic abc123");

        await middleware.InvokeAsync(context, new FakeTokenService());

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    [Fact]
    public async Task Bearer后无Token_返回401()
    {
        var middleware = new TokenAuthMiddleware(_ => Task.CompletedTask);
        var context = CreateContext("/api/files/tree", "Bearer ");

        await middleware.InvokeAsync(context, new FakeTokenService());

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    // ============================================================
    // Token 验证（经 ITokenService 适配）
    // ============================================================

    [Fact]
    public async Task Token正确_通过认证并调用下一中间件()
    {
        bool nextCalled = false;
        var middleware = new TokenAuthMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });
        var context = CreateContext("/api/files/tree", $"Bearer unit-test-token");

        await middleware.InvokeAsync(context, new FakeTokenService { ValidationResult = TokenValidationResult.Valid });

        Assert.True(nextCalled);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Fact]
    public async Task Token无效_返回401()
    {
        var middleware = new TokenAuthMiddleware(_ => Task.CompletedTask);
        var context = CreateContext("/api/files/tree", "Bearer wrong-token");

        await middleware.InvokeAsync(context, new FakeTokenService { ValidationResult = TokenValidationResult.Invalid });

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        using var body = await ReadResponseBody(context);
        Assert.Equal(HttpErrorCode.UNAUTHORIZED.Code,
            body.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Token未配置_返回503()
    {
        var middleware = new TokenAuthMiddleware(_ => Task.CompletedTask);
        var context = CreateContext("/api/files/tree", "Bearer unit-test-token");

        await middleware.InvokeAsync(context, new FakeTokenService { ValidationResult = TokenValidationResult.NotInitialized });

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        using var body = await ReadResponseBody(context);
        Assert.Equal(HttpErrorCode.SERVICE_UNAVAILABLE.Code,
            body.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task DeviceId格式无效_返回400()
    {
        var middleware = new TokenAuthMiddleware(_ => Task.CompletedTask);
        var context = CreateContext("/api/files/tree", "Bearer unit-test-token", "bad device id!");

        await middleware.InvokeAsync(context,
            new FakeTokenService { ValidationResult = TokenValidationResult.Valid, EnsureDeviceResult = false });

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        using var body = await ReadResponseBody(context);
        Assert.Equal(HttpErrorCode.INVALID_DEVICE_ID.Code,
            body.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task 有效DeviceId_通过认证并注册设备()
    {
        // T-025：设备格式校验/自动注册/LastSeen 收敛在 ITokenService；中间件只负责把 deviceId 写入 context.Items
        bool nextCalled = false;
        var tokenService = new FakeTokenService
        {
            ValidationResult = TokenValidationResult.Valid,
            EnsureDeviceResult = true
        };
        var middleware = new TokenAuthMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });
        var context = CreateContext("/api/files/tree", "Bearer unit-test-token", "device-abc");

        await middleware.InvokeAsync(context, tokenService);

        Assert.True(nextCalled);
        Assert.Equal("device-abc", context.Items["DeviceId"]);
        Assert.Equal(1, tokenService.EnsureDeviceCalls);
        Assert.Equal("device-abc", tokenService.LastDeviceId);
        Assert.Null(tokenService.LastOnline); // HTTP 路径 online=null，不更新 Online
    }

    // ============================================================
    // 公开端点跳过认证
    // ============================================================

    [Theory]
    [InlineData("/api/health")]
    [InlineData("/share/abc123")]
    [InlineData("/ws")]
    public async Task 公开端点_无Token_跳过认证(string path)
    {
        bool nextCalled = false;
        var middleware = new TokenAuthMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });
        var context = CreateContext(path);

        await middleware.InvokeAsync(context, new FakeTokenService());

        Assert.True(nextCalled);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Theory]
    [InlineData("/api/files/tree")]
    [InlineData("/api/files/download")]
    public async Task 业务端点_无Token_返回401(string path)
    {
        bool nextCalled = false;
        var middleware = new TokenAuthMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });
        var context = CreateContext(path);

        await middleware.InvokeAsync(context, new FakeTokenService());

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.False(nextCalled);
    }

    // ============================================================
    // 辅助类型与方法
    // ============================================================

    /// <summary>FakeTokenService——中间件只测 HTTP 适配，认证/设备逻辑由 TokenServiceTests 覆盖。</summary>
    private sealed class FakeTokenService : ITokenService
    {
        public TokenValidationResult ValidationResult { get; init; } = TokenValidationResult.Valid;
        public bool EnsureDeviceResult { get; init; } = true;
        public int EnsureDeviceCalls { get; private set; }
        public string? LastDeviceId { get; private set; }
        public bool? LastOnline { get; private set; }

        public Task<TokenValidationResult> ValidateTokenAsync(string token) => Task.FromResult(ValidationResult);

        public Task<bool> EnsureDeviceAsync(string deviceId, bool? online = null)
        {
            EnsureDeviceCalls++;
            LastDeviceId = deviceId;
            LastOnline = online;
            return Task.FromResult(EnsureDeviceResult);
        }

        public Task<string> RotateAsync(bool disconnectAllClients) => Task.FromResult("");

        public Task<string?> GetCurrentTokenAsync() => Task.FromResult<string?>(null);

        // T-072：TokenService 服务定位器消除后 ITokenService 新增 TokenRotated 事件，fake 提供空实现
#pragma warning disable CS0067 // 事件从未使用：fake 仅为满足接口，测试不触发轮换事件
        public event Func<string, Task>? TokenRotated;
#pragma warning restore CS0067
    }

    /// <summary>构造带请求路径、可选认证头/设备 ID 的 HttpContext。</summary>
    private static HttpContext CreateContext(string path, string? authHeader = null, string? deviceId = null)
    {
        DefaultHttpContext context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get; // 真实请求始终带有方法（DefaultHttpContext 默认空串）
        context.Request.Path = path;
        context.RequestServices = new ServiceCollection().BuildServiceProvider();
        context.Response.Body = new MemoryStream();
        if (authHeader != null)
        {
            context.Request.Headers.Authorization = authHeader;
        }

        if (deviceId != null)
        {
            context.Request.Headers["X-Device-Id"] = deviceId;
        }

        return context;
    }

    /// <summary>读取中间件写入的响应体并解析为 JSON。</summary>
    private static async Task<JsonDocument> ReadResponseBody(HttpContext context)
    {
        context.Response.Body.Position = 0;
        using StreamReader reader = new StreamReader(context.Response.Body);
        string json = await reader.ReadToEndAsync();
        return JsonDocument.Parse(json);
    }
}
