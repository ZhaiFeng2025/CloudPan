using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloudPan.Contract;
using CloudPan.Infrastructure.Models;
using CloudPan.Infrastructure.Persistence;
using CloudPan.Server.Host.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CloudPan.Tests.Server.Services;

/// <summary>
/// TokenAuthMiddleware 单元测试——直接构造 HttpContext 调用中间件，
/// 验证 401/503 响应、Token 校验与公开端点跳过认证。
/// 使用 TestBase 提供的临时目录和 SQLite 数据库（种子数据不含 token_hash，除非显式配置）。
/// </summary>
public class TokenAuthMiddlewareTests : Infrastructure.TestBase
{
    private const string TestToken = "unit-test-token";

    // ============================================================
    // 无 Authorization 头
    // ============================================================

    [Fact]
    public async Task 无Authorization头_返回401()
    {
        // Arrange
        var dbFactory = CreateServerDbFactory();
        using var sp = BuildServiceProvider(dbFactory);
        bool nextCalled = false;
        TokenAuthMiddleware middleware = new TokenAuthMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });
        var context = CreateContext(sp, "/api/files/tree");

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.False(nextCalled);
        using var body = await ReadResponseBody(context);
        Assert.Equal(HttpErrorCode.UNAUTHORIZED.Code,
            body.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task 非Bearer格式头_返回401()
    {
        // Arrange
        var dbFactory = CreateServerDbFactory();
        using var sp = BuildServiceProvider(dbFactory);
        TokenAuthMiddleware middleware = new TokenAuthMiddleware(_ => Task.CompletedTask);
        var context = CreateContext(sp, "/api/files/tree", "Basic abc123");

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    [Fact]
    public async Task Bearer后无Token_返回401()
    {
        // Arrange
        var dbFactory = CreateServerDbFactory();
        using var sp = BuildServiceProvider(dbFactory);
        TokenAuthMiddleware middleware = new TokenAuthMiddleware(_ => Task.CompletedTask);
        var context = CreateContext(sp, "/api/files/tree", "Bearer ");

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    // ============================================================
    // Token 验证
    // ============================================================

    [Fact]
    public async Task Token正确_通过认证并调用下一中间件()
    {
        // Arrange
        var dbFactory = CreateServerDbFactory();
        await ConfigureTokenAsync(dbFactory, TestToken);
        using var sp = BuildServiceProvider(dbFactory);
        bool nextCalled = false;
        TokenAuthMiddleware middleware = new TokenAuthMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });
        var context = CreateContext(sp, "/api/files/tree", $"Bearer {TestToken}");

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        Assert.True(nextCalled);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Fact]
    public async Task Token无效_返回401()
    {
        // Arrange
        var dbFactory = CreateServerDbFactory();
        await ConfigureTokenAsync(dbFactory, TestToken);
        using var sp = BuildServiceProvider(dbFactory);
        TokenAuthMiddleware middleware = new TokenAuthMiddleware(_ => Task.CompletedTask);
        var context = CreateContext(sp, "/api/files/tree", "Bearer wrong-token");

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        using var body = await ReadResponseBody(context);
        Assert.Equal(HttpErrorCode.UNAUTHORIZED.Code,
            body.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Token未配置_返回503()
    {
        // Arrange：种子数据只有 global_version，未写入 token_hash
        var dbFactory = CreateServerDbFactory();
        using var sp = BuildServiceProvider(dbFactory);
        TokenAuthMiddleware middleware = new TokenAuthMiddleware(_ => Task.CompletedTask);
        var context = CreateContext(sp, "/api/files/tree", $"Bearer {TestToken}");

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        using var body = await ReadResponseBody(context);
        Assert.Equal(HttpErrorCode.SERVICE_UNAVAILABLE.Code,
            body.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task DeviceId格式无效_返回400()
    {
        // Arrange
        var dbFactory = CreateServerDbFactory();
        await ConfigureTokenAsync(dbFactory, TestToken);
        using var sp = BuildServiceProvider(dbFactory);
        TokenAuthMiddleware middleware = new TokenAuthMiddleware(_ => Task.CompletedTask);
        var context = CreateContext(sp, "/api/files/tree", $"Bearer {TestToken}", "bad device id!");

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        using var body = await ReadResponseBody(context);
        Assert.Equal(HttpErrorCode.INVALID_DEVICE_ID.Code,
            body.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task 有效DeviceId_通过认证并注册设备()
    {
        // Arrange
        var dbFactory = CreateServerDbFactory();
        await ConfigureTokenAsync(dbFactory, TestToken);
        using var sp = BuildServiceProvider(dbFactory);
        bool nextCalled = false;
        TokenAuthMiddleware middleware = new TokenAuthMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });
        var context = CreateContext(sp, "/api/files/tree", $"Bearer {TestToken}", "device-abc");

        // Act
        await middleware.InvokeAsync(context);

        // Assert：通过认证，DeviceId 写入 Items，设备自动注册
        Assert.True(nextCalled);
        Assert.Equal("device-abc", context.Items["DeviceId"]);

        await using var db = await dbFactory.CreateDbContextAsync();
        var device = await db.Devices.FirstOrDefaultAsync(d => d.Id == "device-abc");
        Assert.NotNull(device);
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
        // Arrange
        var dbFactory = CreateServerDbFactory();
        using var sp = BuildServiceProvider(dbFactory);
        bool nextCalled = false;
        TokenAuthMiddleware middleware = new TokenAuthMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });
        var context = CreateContext(sp, path);

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        Assert.True(nextCalled);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Theory]
    [InlineData("/api/files/tree")]
    [InlineData("/api/files/download")]
    public async Task 业务端点_无Token_返回401(string path)
    {
        // Arrange
        var dbFactory = CreateServerDbFactory();
        using var sp = BuildServiceProvider(dbFactory);
        bool nextCalled = false;
        TokenAuthMiddleware middleware = new TokenAuthMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });
        var context = CreateContext(sp, path);

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.False(nextCalled);
    }

    // ============================================================
    // 辅助方法
    // ============================================================

    /// <summary>构造含内存缓存/DB 工厂/日志服务的 ServiceProvider。</summary>
    private static ServiceProvider BuildServiceProvider(IDbContextFactory<CloudPanDbContext> dbFactory)
    {
        ServiceCollection services = new ServiceCollection();
        services.AddMemoryCache();
        services.AddSingleton(dbFactory);
        services.AddLogging();
        return services.BuildServiceProvider();
    }

    /// <summary>构造带请求路径、可选认证头/设备 ID 的 HttpContext。</summary>
    private static HttpContext CreateContext(ServiceProvider sp, string path, string? authHeader = null, string? deviceId = null)
    {
        DefaultHttpContext context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get; // 真实请求始终带有方法（DefaultHttpContext 默认空串）
        context.Request.Path = path;
        context.RequestServices = sp;
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

    /// <summary>将 token 的 SHA-256 哈希写入 AppConfig.token_hash。</summary>
    private static async Task ConfigureTokenAsync(IDbContextFactory<CloudPanDbContext> dbFactory, string token)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        db.AppConfigs.Add(new AppConfig { Key = "token_hash", Value = ComputeSha256(token) });
        await db.SaveChangesAsync();
    }

    /// <summary>读取中间件写入的响应体并解析为 JSON。</summary>
    private static async Task<JsonDocument> ReadResponseBody(HttpContext context)
    {
        context.Response.Body.Position = 0;
        using StreamReader reader = new StreamReader(context.Response.Body);
        string json = await reader.ReadToEndAsync();
        return JsonDocument.Parse(json);
    }

    /// <summary>计算 SHA-256（64 字符小写十六进制）——与中间件实现一致。</summary>
    private static string ComputeSha256(string input)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(input);
        byte[] hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
