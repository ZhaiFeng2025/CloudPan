using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using CloudPan.Contract;
using CloudPan.Server.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CloudPan.Tests.Server.Core;

/// <summary>
/// WebSocketHandler 拆分后（T-111）会话三阶段单测：认证失败/重复连接替换/断连清理。
/// WebSocketSession/WebSocketConnectionRegistry 为 internal 协作类，经 InternalsVisibleTo 直接测试；
/// 用内存回环 TCP + WebSocket.CreateFromStream 建立真实 WebSocket 对，脱离 HTTP/宿主。
/// </summary>
public class WebSocketHandlerTests
{
    private const string ValidToken = "valid-token-test";

    // ────────────────────────────────────────────────────────────
    // 认证失败
    // ────────────────────────────────────────────────────────────

    /// <summary>错误 Token → 发送 auth_error 且不注册连接。</summary>
    [Fact]
    public async Task 认证失败_错误Token_发送AuthError且不注册()
    {
        var (server, client) = await CreateWebSocketPairAsync();
        var tokenService = new FakeTokenService(ValidToken);
        var registry = new WebSocketConnectionRegistry(tokenService, NullLogger<WebSocketHandler>.Instance);
        var session = new WebSocketSession(registry, tokenService, NullLogger<WebSocketHandler>.Instance, server);
        Task runTask = session.RunAsync();
        try
        {
            await SendTextAsync(client, JsonSerializer.Serialize(new { token = "wrong-token", deviceId = "dev-1" }));
            var msg = await ReceiveTextAsync(client);
            Assert.Equal(WebSocketEvent.AuthError, GetType(msg));
            Assert.Empty(registry.Connections);
        }
        finally
        {
            client.Dispose(); // 解除服务端 CloseSafeAsync 的 Close 回执等待
            await AwaitWithTimeoutAsync(runTask);
            server.Dispose();
        }
    }

    /// <summary>缺少 deviceId → 发送 auth_error 且不注册连接。</summary>
    [Fact]
    public async Task 认证失败_缺少DeviceId_发送AuthError且不注册()
    {
        var (server, client) = await CreateWebSocketPairAsync();
        var tokenService = new FakeTokenService(ValidToken);
        var registry = new WebSocketConnectionRegistry(tokenService, NullLogger<WebSocketHandler>.Instance);
        var session = new WebSocketSession(registry, tokenService, NullLogger<WebSocketHandler>.Instance, server);
        Task runTask = session.RunAsync();
        try
        {
            await SendTextAsync(client, JsonSerializer.Serialize(new { token = ValidToken }));
            var msg = await ReceiveTextAsync(client);
            Assert.Equal(WebSocketEvent.AuthError, GetType(msg));
            Assert.Empty(registry.Connections);
        }
        finally
        {
            client.Dispose();
            await AwaitWithTimeoutAsync(runTask);
            server.Dispose();
        }
    }

    // ────────────────────────────────────────────────────────────
    // 断连清理
    // ────────────────────────────────────────────────────────────

    /// <summary>客户端正常关闭 → 连接移除且设备置离线。</summary>
    [Fact]
    public async Task 断连清理_客户端关闭_移除连接并置离线()
    {
        var (server, client) = await CreateWebSocketPairAsync();
        var tokenService = new FakeTokenService(ValidToken);
        var registry = new WebSocketConnectionRegistry(tokenService, NullLogger<WebSocketHandler>.Instance);
        var session = new WebSocketSession(registry, tokenService, NullLogger<WebSocketHandler>.Instance, server);
        Task runTask = session.RunAsync();
        try
        {
            await SendTextAsync(client, JsonSerializer.Serialize(new { token = ValidToken, deviceId = "dev-1" }));
            var auth = await ReceiveTextAsync(client);
            Assert.Equal(WebSocketEvent.AuthOk, GetType(auth));
            Assert.Single(registry.Connections);

            // 客户端正常发起 Close → 服务端接收循环读到 Close → CleanupAsync
            await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
            await AwaitWithTimeoutAsync(runTask);

            Assert.Empty(registry.Connections);
            Assert.Contains(tokenService.OnlineUpdates, u => u.DeviceId == "dev-1" && u.Online == false);
        }
        finally
        {
            client.Dispose();
            server.Dispose();
        }
    }

    // ────────────────────────────────────────────────────────────
    // 重复连接替换（CLAUDE.md 7.4 竞态路径）
    // ────────────────────────────────────────────────────────────

    /// <summary>同设备第二连接替换第一连接；旧连接断连清理不误删新连接。</summary>
    [Fact]
    public async Task 重复连接_替换旧连接_清理不误删新连接()
    {
        var (serverA, clientA) = await CreateWebSocketPairAsync();
        var (serverB, clientB) = await CreateWebSocketPairAsync();
        var tokenService = new FakeTokenService(ValidToken);
        var registry = new WebSocketConnectionRegistry(tokenService, NullLogger<WebSocketHandler>.Instance);
        var sessionA = new WebSocketSession(registry, tokenService, NullLogger<WebSocketHandler>.Instance, serverA);
        try
        {
            // 第一个连接注册
            WebSocketConnection connA = await registry.RegisterAsync(serverA, "dev-1");
            Assert.Single(registry.Connections);
            Assert.True(ReferenceEquals(connA, registry.Connections["dev-1"]));

            // 后台回执：响应 serverA 被替换关闭时的 Close（避免 CloseAsync 等待回执）
            Task echoA = EchoCloseAsync(clientA);

            // 第二个同设备连接注册 → 替换旧连接（关闭 serverA）
            WebSocketConnection connB = await registry.RegisterAsync(serverB, "dev-1");
            Assert.Single(registry.Connections);
            Assert.True(ReferenceEquals(connB, registry.Connections["dev-1"]));
            await echoA;

            // 旧连接断连清理：连接池中已是新连接（引用不同）→ 跳过移除，新连接保留（7.4 竞态路径不回归）
            await sessionA.CleanupAsync(connA);
            Assert.Single(registry.Connections);
            Assert.True(ReferenceEquals(connB, registry.Connections["dev-1"]));

            // 新连接清理：正常移除
            Task echoB = EchoCloseAsync(clientB);
            var sessionB = new WebSocketSession(registry, tokenService, NullLogger<WebSocketHandler>.Instance, serverB);
            await sessionB.CleanupAsync(connB);
            await echoB;
            Assert.Empty(registry.Connections);
        }
        finally
        {
            clientA.Dispose();
            serverA.Dispose();
            clientB.Dispose();
            serverB.Dispose();
        }
    }

    // ────────────────────────────────────────────────────────────
    // 工具方法
    // ────────────────────────────────────────────────────────────

    /// <summary>内存回环建立服务端/客户端真实 WebSocket 对（TcpListener + WebSocket.CreateFromStream）。</summary>
    private static async Task<(WebSocket Server, WebSocket Client)> CreateWebSocketPairAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        Task<TcpClient> acceptTask = listener.AcceptTcpClientAsync();
        var tcpClient = new TcpClient();
        await tcpClient.ConnectAsync(IPAddress.Loopback, port);
        var tcpServer = await acceptTask;
        listener.Stop();

        WebSocket server = WebSocket.CreateFromStream(tcpServer.GetStream(), isServer: true, null, TimeSpan.FromSeconds(30));
        WebSocket client = WebSocket.CreateFromStream(tcpClient.GetStream(), isServer: false, null, TimeSpan.FromSeconds(30));
        return (server, client);
    }

    /// <summary>后台发起 Close 回执（解除对方 CloseAsync 的等待）。</summary>
    private static async Task EchoCloseAsync(WebSocket socket)
    {
        try
        {
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
        }
        catch
        {
            // 连接已断开/已关闭，忽略
        }
    }

    /// <summary>等待任务完成，超时抛出（防止测试静默挂起）。</summary>
    private static async Task AwaitWithTimeoutAsync(Task task, int timeoutMs = 10_000)
    {
        Task completed = await Task.WhenAny(task, Task.Delay(timeoutMs));
        if (completed != task)
        {
            throw new TimeoutException($"等待任务超时（{timeoutMs}ms）");
        }
        await task; // 传播异常
    }

    private static async Task SendTextAsync(WebSocket socket, string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
    }

    /// <summary>读取一条完整文本消息（聚合分片），超时返回 null。</summary>
    private static async Task<string?> ReceiveTextAsync(WebSocket socket, TimeSpan? timeout = null)
    {
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(5));
        byte[] buffer = new byte[8192];
        using var ms = new MemoryStream();
        WebSocketReceiveResult result;
        try
        {
            do
            {
                result = await socket.ReceiveAsync(buffer, cts.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    throw new WebSocketException($"服务端关闭连接: {result.CloseStatus} {result.CloseStatusDescription}");
                }
                ms.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);
        }
        catch (OperationCanceledException)
        {
            return null; // 超时无消息
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static string GetType(string? json)
    {
        if (string.IsNullOrEmpty(json)) return "";
        using JsonDocument doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("type").GetString() ?? "";
    }

    /// <summary>ITokenService 测试桩：校验合法 Token、记录在线状态更新。</summary>
    private sealed class FakeTokenService : ITokenService
    {
        private readonly string _validToken;

        internal FakeTokenService(string validToken) => _validToken = validToken;

        internal List<(string DeviceId, bool? Online)> OnlineUpdates { get; } = new();

        public Task<TokenValidationResult> ValidateTokenAsync(string token) =>
            Task.FromResult(token == _validToken ? TokenValidationResult.Valid : TokenValidationResult.Invalid);

        public Task<bool> EnsureDeviceAsync(string deviceId, bool? online = null)
        {
            OnlineUpdates.Add((deviceId, online));
            return Task.FromResult(true);
        }

        public Task<string> RotateAsync(bool disconnectAllClients) => throw new NotSupportedException("测试桩不支持");
        public Task<string?> GetCurrentTokenAsync() => throw new NotSupportedException("测试桩不支持");

        // 显式访问器实现（避免 CS0067 事件未使用）：测试桩不触发轮换，仅满足接口签名
        private Func<string, Task>? _tokenRotated;
        public event Func<string, Task>? TokenRotated
        {
            add => _tokenRotated += value;
            remove => _tokenRotated -= value;
        }
    }
}
