using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using CloudPan.Infrastructure.Models;
using CloudPan.Infrastructure.Persistence;
using CloudPan.Infrastructure.Security;
using CloudPan.Server.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CloudPan.Tests.Server.Services;

/// <summary>
/// TokenService（轮换 + 认证 + 设备注册，F-25/T-025 单一事实来源）单元测试。
/// 覆盖：轮换副作用顺序、ValidateTokenAsync（HTTP 与 WS 两路径共用，结果一致）、
/// EnsureDeviceAsync（自动注册/LastSeen 幂等/格式校验/Online 参数）。
/// </summary>
public class TokenServiceTests : Infrastructure.TestBase
{
    private const string TestToken = "unit-test-token";

    private sealed class FakeWebSocketHandler : IWebSocketHandler
    {
        public int DisconnectAllCalls;

        public Task HandleConnectionAsync(WebSocket socket) => Task.CompletedTask;
        public Task BroadcastFileChangedAsync(string path, int version, string? excludeDeviceId = null) => Task.CompletedTask;
        public Task BroadcastFileDeletedAsync(string path, string? excludeDeviceId = null) => Task.CompletedTask;
        public Task BroadcastFileRenamedAsync(string oldPath, string newPath, string? excludeDeviceId = null) => Task.CompletedTask;
        public Task DisconnectAllAsync(string reason) { DisconnectAllCalls++; return Task.CompletedTask; }
        public int ActiveConnectionCount => 0;
    }

    private static TokenService CreateService(
        IDbContextFactory<CloudPanDbContext> dbFactory, string syncRoot, IMemoryCache cache, FakeWebSocketHandler ws)
    {
        // T-022：TokenService 的 token_hash 读写统一经 ISettingsService
        var settingsService = new SettingsService(dbFactory);
        // T-025：IWebSocketHandler 经 IServiceProvider 延迟解析，打破 WebSocketHandler ↔ ITokenService 循环依赖。
        // 测试用 provider 不 Dispose（TokenService 持引用，RotateAsync 轮换时需解析 IWebSocketHandler）。
        var provider = new ServiceCollection()
            .AddSingleton<IWebSocketHandler>(ws)
            .BuildServiceProvider();
        return new TokenService(dbFactory, settingsService, syncRoot, cache, provider, NullLogger<TokenService>.Instance);
    }

    /// <summary>将 token 的 SHA-256 哈希写入 AppConfig.token_hash（模拟服务端初始化/轮换后的状态）。</summary>
    private static async Task ConfigureTokenHashAsync(IDbContextFactory<CloudPanDbContext> dbFactory, string token)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        db.AppConfigs.Add(new AppConfig { Key = "token_hash", Value = ComputeSha256(token) });
        await db.SaveChangesAsync();
    }

    /// <summary>计算 SHA-256（64 字符小写十六进制）。</summary>
    private static string ComputeSha256(string input)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    // ============================================================
    // 轮换（既有覆盖，T-022 收敛后保持）
    // ============================================================

    [Fact]
    public async Task RotateAsync_生成64hex_同步DB哈希与token文件()
    {
        var dbFactory = CreateServerDbFactory();
        var cache = new MemoryCache(new MemoryCacheOptions());
        var ws = new FakeWebSocketHandler();
        var service = CreateService(dbFactory, TempDir, cache, ws);

        string token = await service.RotateAsync(disconnectAllClients: false);

        // 64 位小写十六进制
        Assert.Equal(64, token.Length);
        Assert.True(token.All(Uri.IsHexDigit));

        // DB token_hash = SHA-256(token) 小写
        await using var db = await dbFactory.CreateDbContextAsync();
        string? storedHash = await db.AppConfigs
            .Where(c => c.Key == "token_hash").Select(c => c.Value).FirstOrDefaultAsync();
        string expectedHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
        Assert.Equal(expectedHash, storedHash);

        // token.txt 明文一致
        Assert.Equal(token, SecretStore.ReadToken(TempDir));

        // 默认不踢连接
        Assert.Equal(0, ws.DisconnectAllCalls);
    }

    [Fact]
    public async Task RotateAsync_断开选项_控制踢连接()
    {
        var dbFactory = CreateServerDbFactory();
        var cache = new MemoryCache(new MemoryCacheOptions());
        var ws = new FakeWebSocketHandler();
        var service = CreateService(dbFactory, TempDir, cache, ws);

        await service.RotateAsync(disconnectAllClients: true);
        Assert.Equal(1, ws.DisconnectAllCalls);

        await service.RotateAsync(disconnectAllClients: false);
        Assert.Equal(1, ws.DisconnectAllCalls); // 第二次不踢
    }

    [Fact]
    public async Task RotateAsync_立即失效token哈希缓存()
    {
        var dbFactory = CreateServerDbFactory();
        var cache = new MemoryCache(new MemoryCacheOptions());
        var ws = new FakeWebSocketHandler();
        var service = CreateService(dbFactory, TempDir, cache, ws);

        // 预置"旧"缓存值，模拟中间件已缓存旧哈希（5 分钟窗口）
        cache.Set(CacheKeys.TokenHash, "old-hash");

        await service.RotateAsync(disconnectAllClients: false);

        Assert.Null(cache.Get(CacheKeys.TokenHash)); // 旧 Token 即刻失效
    }

    [Fact]
    public async Task GetCurrentTokenAsync_读token文件()
    {
        var dbFactory = CreateServerDbFactory();
        var cache = new MemoryCache(new MemoryCacheOptions());
        var ws = new FakeWebSocketHandler();
        var service = CreateService(dbFactory, TempDir, cache, ws);

        Assert.Null(await service.GetCurrentTokenAsync());

        SecretStore.WriteToken("unit-token-123", TempDir);
        Assert.Equal("unit-token-123", await service.GetCurrentTokenAsync());
    }

    // ============================================================
    // ValidateTokenAsync（HTTP 与 WS 两路径共用，T-025）
    // ============================================================

    [Fact]
    public async Task ValidateTokenAsync_正确Token_返回Valid()
    {
        var dbFactory = CreateServerDbFactory();
        await ConfigureTokenHashAsync(dbFactory, TestToken);
        var service = CreateService(dbFactory, TempDir, new MemoryCache(new MemoryCacheOptions()), new FakeWebSocketHandler());

        var result = await service.ValidateTokenAsync(TestToken);

        Assert.Equal(TokenValidationResult.Valid, result);
    }

    [Fact]
    public async Task ValidateTokenAsync_错误Token_返回Invalid()
    {
        var dbFactory = CreateServerDbFactory();
        await ConfigureTokenHashAsync(dbFactory, TestToken);
        var service = CreateService(dbFactory, TempDir, new MemoryCache(new MemoryCacheOptions()), new FakeWebSocketHandler());

        var result = await service.ValidateTokenAsync("wrong-token");

        Assert.Equal(TokenValidationResult.Invalid, result);
    }

    [Fact]
    public async Task ValidateTokenAsync_未配置tokenHash_返回NotInitialized()
    {
        // 种子数据只有 global_version，未写入 token_hash → NotInitialized（中间件映射为 503）
        var dbFactory = CreateServerDbFactory();
        var service = CreateService(dbFactory, TempDir, new MemoryCache(new MemoryCacheOptions()), new FakeWebSocketHandler());

        var result = await service.ValidateTokenAsync(TestToken);

        Assert.Equal(TokenValidationResult.NotInitialized, result);
    }

    [Fact]
    public async Task ValidateTokenAsync_同一Token_HTTP与WS两路径共用校验_结果一致()
    {
        // T-025 目标：TokenAuthMiddleware（HTTP）与 WebSocketHandler（WS）认证共用同一 ValidateTokenAsync，
        // 同一 token 无论经哪个路径校验，结果一致——认证行为不再分叉。
        var dbFactory = CreateServerDbFactory();
        await ConfigureTokenHashAsync(dbFactory, TestToken);
        var service = CreateService(dbFactory, TempDir, new MemoryCache(new MemoryCacheOptions()), new FakeWebSocketHandler());

        // 模拟 HTTP 中间件调用
        TokenValidationResult httpResult = await service.ValidateTokenAsync(TestToken);
        // 模拟 WebSocketHandler 调用
        TokenValidationResult wsResult = await service.ValidateTokenAsync(TestToken);
        TokenValidationResult wsWrongResult = await service.ValidateTokenAsync("wrong-token");

        Assert.Equal(TokenValidationResult.Valid, httpResult);
        Assert.Equal(httpResult, wsResult); // 两路径对同一合法 token 结果一致
        Assert.Equal(TokenValidationResult.Invalid, wsWrongResult);
    }

    [Fact]
    public async Task ValidateTokenAsync_缓存命中_不重复查库()
    {
        // 5 分钟内存缓存收敛在服务内：首次查库后，绕过服务直接改 DB 哈希，缓存未过期仍按旧值校验
        var dbFactory = CreateServerDbFactory();
        await ConfigureTokenHashAsync(dbFactory, TestToken);
        var service = CreateService(dbFactory, TempDir, new MemoryCache(new MemoryCacheOptions()), new FakeWebSocketHandler());

        Assert.Equal(TokenValidationResult.Valid, await service.ValidateTokenAsync(TestToken));

        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            var cfg = await db.AppConfigs.FindAsync("token_hash");
            Assert.NotNull(cfg);
            cfg!.Value = ComputeSha256("other-token");
            await db.SaveChangesAsync();
        }

        Assert.Equal(TokenValidationResult.Valid, await service.ValidateTokenAsync(TestToken)); // 缓存未过期
    }

    // ============================================================
    // EnsureDeviceAsync（设备自动注册 + LastSeen 维护，T-025）
    // ============================================================

    [Fact]
    public async Task EnsureDeviceAsync_新设备_自动注册并设置LastSeen()
    {
        var dbFactory = CreateServerDbFactory();
        var service = CreateService(dbFactory, TempDir, new MemoryCache(new MemoryCacheOptions()), new FakeWebSocketHandler());

        bool ok = await service.EnsureDeviceAsync("device-new-001");

        Assert.True(ok);
        await using var db = await dbFactory.CreateDbContextAsync();
        var device = await db.Devices.FirstOrDefaultAsync(d => d.Id == "device-new-001");
        Assert.NotNull(device);
        Assert.Equal("device-new-001", device!.Id);
        Assert.StartsWith("设备-", device.Name);
        Assert.Equal(0, device.Online); // HTTP 路径（online=null）不更新 Online
        Assert.NotNull(device.RegisteredAt);
    }

    [Fact]
    public async Task EnsureDeviceAsync_已存在设备_更新LastSeen不重复注册()
    {
        var dbFactory = CreateServerDbFactory();
        var service = CreateService(dbFactory, TempDir, new MemoryCache(new MemoryCacheOptions()), new FakeWebSocketHandler());

        await service.EnsureDeviceAsync("device-abc");
        bool ok = await service.EnsureDeviceAsync("device-abc"); // 第二次调用应幂等

        Assert.True(ok);
        await using var db = await dbFactory.CreateDbContextAsync();
        Assert.Equal(1, await db.Devices.CountAsync(d => d.Id == "device-abc")); // 无重复行
    }

    [Fact]
    public async Task EnsureDeviceAsync_重复调用_幂等不抛异常()
    {
        var dbFactory = CreateServerDbFactory();
        var service = CreateService(dbFactory, TempDir, new MemoryCache(new MemoryCacheOptions()), new FakeWebSocketHandler());

        await service.EnsureDeviceAsync("device-idempotent");
        bool ok = await service.EnsureDeviceAsync("device-idempotent");
        await service.EnsureDeviceAsync("device-idempotent");

        Assert.True(ok);
        await using var db = await dbFactory.CreateDbContextAsync();
        Assert.Equal(1, await db.Devices.CountAsync(d => d.Id == "device-idempotent"));
    }

    [Fact]
    public async Task EnsureDeviceAsync_格式非法_返回false()
    {
        var dbFactory = CreateServerDbFactory();
        var service = CreateService(dbFactory, TempDir, new MemoryCache(new MemoryCacheOptions()), new FakeWebSocketHandler());

        Assert.False(await service.EnsureDeviceAsync(""));     // 空
        Assert.False(await service.EnsureDeviceAsync("bad device id!")); // 含空格
        Assert.False(await service.EnsureDeviceAsync("../../etc")); // 路径穿越字符
        Assert.False(await service.EnsureDeviceAsync("12345678901234567890123456789012345678901234567890123456789012345")); // >64

        // 非法设备不落库（TestBase 种子预置 "server" 设备，故按非法 ID 逐一断言不存在）
        await using var db = await dbFactory.CreateDbContextAsync();
        Assert.Null(await db.Devices.FindAsync("bad device id!"));
        Assert.Null(await db.Devices.FindAsync("../../etc"));
        Assert.Null(await db.Devices.FindAsync("12345678901234567890123456789012345678901234567890123456789012345"));
    }

    [Fact]
    public async Task EnsureDeviceAsync_在线参数_更新Online状态()
    {
        var dbFactory = CreateServerDbFactory();
        var service = CreateService(dbFactory, TempDir, new MemoryCache(new MemoryCacheOptions()), new FakeWebSocketHandler());

        // WebSocket 连接：online=true
        await service.EnsureDeviceAsync("ws-device-001", online: true);
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            var d = await db.Devices.FindAsync("ws-device-001");
            Assert.Equal(1, d!.Online);
        }

        // WebSocket 断开：online=false
        await service.EnsureDeviceAsync("ws-device-001", online: false);
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            var d = await db.Devices.FindAsync("ws-device-001");
            Assert.Equal(0, d!.Online);
        }

        // HTTP 路径（online=null）不改变 Online
        await service.EnsureDeviceAsync("ws-device-001", online: null);
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            var d = await db.Devices.FindAsync("ws-device-001");
            Assert.Equal(0, d!.Online);
        }
    }
}
