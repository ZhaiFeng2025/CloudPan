using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using CloudPan.Server.Data;
using CloudPan.Server.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CloudPan.Tests.Server.Services;

/// <summary>
/// TokenService（轮换）单元测试——验证副作用顺序与一致性策略：
/// 新 Token 生成 → token.txt → DB token_hash → 缓存立即失效 → 可选断开连接。
/// </summary>
public class TokenServiceTests : Infrastructure.TestBase
{
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
        => new TokenService(dbFactory, syncRoot, cache, ws, NullLogger<TokenService>.Instance);

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
}
