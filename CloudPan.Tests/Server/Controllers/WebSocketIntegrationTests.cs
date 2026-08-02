using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using CloudPan.Contract;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace CloudPan.Tests.Server.Controllers;

/// <summary>
/// WebSocket 集成测试——验证 /ws 端点的消息级认证与 file_changed 实时推送链路。
/// 补齐原有测试缺口：此前 /ws 仅作为"公开端点"出现，从未验证真实握手/认证/事件广播。
/// </summary>
public class WebSocketIntegrationTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _tempDir;
    private const string TestToken = "test-token-ws-integration";
    private const string WsDeviceId = "ws-test-device-001";
    private const string UploadDeviceId = "ws-uploader-001";

    public WebSocketIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"CloudPanWsIntegration_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        // 用 UseSetting 注入 Token（而非进程级环境变量），避免与 FilesControllerIntegrationTests
        // 并行运行时互相覆盖 CloudPan__Token 导致认证竞态。UseSetting 优先级高于环境变量配置源。
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("SyncRoot", _tempDir);
            builder.UseSetting("CloudPan:Token", TestToken);
        });
    }

    public void Dispose()
    {
        _factory.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    // ============================================================
    // 消息级认证
    // ============================================================

    /// <summary>合法 Token + 设备 ID → 收到 auth_ok。</summary>
    [Fact]
    public async Task WebSocket_正确Token_收到AuthOk()
    {
        using WebSocket socket = await ConnectAndAuthenticateAsync();
        // 认证成功即返回，无需进一步断言（认证失败会抛异常）

        // 验证连接仍处于打开状态（认证通过后未被关闭）
        Assert.Equal(WebSocketState.Open, socket.State);
    }

    /// <summary>错误 Token → 收到 auth_error。</summary>
    [Fact]
    public async Task WebSocket_错误Token_收到AuthError()
    {
        var client = _factory.Server.CreateWebSocketClient();
        using var socket = await client.ConnectAsync(new Uri("ws://localhost/ws"), CancellationToken.None);

        await SendTextAsync(socket, JsonSerializer.Serialize(new { token = "wrong-token", deviceId = WsDeviceId }));

        var msg = await ReceiveTextAsync(socket);
        Assert.Equal(WebSocketEvent.AuthError, GetType(msg));
    }

    /// <summary>缺少 token → 收到 auth_error。</summary>
    [Fact]
    public async Task WebSocket_缺少Token_收到AuthError()
    {
        var client = _factory.Server.CreateWebSocketClient();
        using var socket = await client.ConnectAsync(new Uri("ws://localhost/ws"), CancellationToken.None);

        await SendTextAsync(socket, JsonSerializer.Serialize(new { deviceId = WsDeviceId }));

        var msg = await ReceiveTextAsync(socket);
        Assert.Equal(WebSocketEvent.AuthError, GetType(msg));
    }

    // ============================================================
    // 实时推送
    // ============================================================

    /// <summary>
    /// 认证成功的 WS 客户端，在另一设备通过 HTTP 上传文件后，应收到 file_changed 事件。
    /// 广播排除上传设备（excludeDeviceId），故 WS 设备必须与上传设备不同。
    /// </summary>
    [Fact]
    public async Task WebSocket_其他设备上传文件_收到FileChanged事件()
    {
        // 1. WS 客户端连接 + 认证
        var client = _factory.Server.CreateWebSocketClient();
        using var socket = await client.ConnectAsync(new Uri("ws://localhost/ws"), CancellationToken.None);
        await SendTextAsync(socket, JsonSerializer.Serialize(new { token = TestToken, deviceId = WsDeviceId }));
        var authResp = await ReceiveTextAsync(socket);
        Assert.Equal(WebSocketEvent.AuthOk, GetType(authResp));

        // 2. 另一设备通过 HTTP 上传文件
        using var httpClient = _factory.CreateClient();
        httpClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", TestToken);
        httpClient.DefaultRequestHeaders.Add("X-Device-Id", UploadDeviceId);

        string remotePath = $"/ws-event-{Guid.NewGuid():N}.txt";
        string content = "ws push event content";
        using var form = new MultipartFormDataContent();
        using (var fs = new MemoryStream(Encoding.UTF8.GetBytes(content)))
        {
            form.Add(new StreamContent(fs), "file", "ws-event.txt");
            form.Add(new StringContent(remotePath), "path");
            form.Add(new StringContent("0"), "baseVersion");
            var upload = await httpClient.PostAsync("/api/files/upload", form);
            upload.EnsureSuccessStatusCode();
        }

        // 3. WS 客户端收到 file_changed 事件（广播同步完成，但给足超时防偶发慢）
        var evt = await ReceiveTextAsync(socket, TimeSpan.FromSeconds(15));
        Assert.NotNull(evt); // 事件必须到达
        using JsonDocument evtDoc = JsonDocument.Parse(evt!);
        Assert.Equal(WebSocketEvent.FileChanged, evtDoc.RootElement.GetProperty("type").GetString());
        Assert.Equal(remotePath, evtDoc.RootElement.GetProperty("path").GetString());
    }

    /// <summary>上传设备与 WS 设备相同 → 不向自身广播（excludeDeviceId 生效）。</summary>
    [Fact]
    public async Task WebSocket_同设备上传_不广播自身事件()
    {
        var client = _factory.Server.CreateWebSocketClient();
        using var socket = await client.ConnectAsync(new Uri("ws://localhost/ws"), CancellationToken.None);
        await SendTextAsync(socket, JsonSerializer.Serialize(new { token = TestToken, deviceId = WsDeviceId }));
        await ReceiveTextAsync(socket); // auth_ok

        // 同一设备上传
        using var httpClient = _factory.CreateClient();
        httpClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", TestToken);
        httpClient.DefaultRequestHeaders.Add("X-Device-Id", WsDeviceId);

        string remotePath = $"/ws-self-{Guid.NewGuid():N}.txt";
        using var form = new MultipartFormDataContent();
        using (var fs = new MemoryStream(Encoding.UTF8.GetBytes("self upload")))
        {
            form.Add(new StreamContent(fs), "file", "ws-self.txt");
            form.Add(new StringContent(remotePath), "path");
            form.Add(new StringContent("0"), "baseVersion");
            var upload = await httpClient.PostAsync("/api/files/upload", form);
            upload.EnsureSuccessStatusCode();
        }

        // 短暂等待后不应收到任何事件（或仅收到非 file_changed 的包，如心跳 ping）
        var msg = await ReceiveTextAsync(socket, TimeSpan.FromSeconds(2));
        Assert.NotEqual(WebSocketEvent.FileChanged, GetType(msg));
    }

    // ============================================================
    // 工具方法
    // ============================================================

    private async Task<WebSocket> ConnectAndAuthenticateAsync()
    {
        var client = _factory.Server.CreateWebSocketClient();
        var socket = await client.ConnectAsync(new Uri("ws://localhost/ws"), CancellationToken.None);
        await SendTextAsync(socket, JsonSerializer.Serialize(new { token = TestToken, deviceId = WsDeviceId }));
        var authResp = await ReceiveTextAsync(socket);
        Assert.Equal(WebSocketEvent.AuthOk, GetType(authResp));
        return socket;
    }

    private static string GetType(string? json)
    {
        if (string.IsNullOrEmpty(json)) return ""; // 超时无消息
        using JsonDocument doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("type").GetString() ?? "";
    }

    private static async Task SendTextAsync(WebSocket socket, string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
    }

    /// <summary>读取一条完整文本消息（聚合分片），超时返回 null；服务器关闭连接时抛出。</summary>
    private static async Task<string?> ReceiveTextAsync(WebSocket socket, TimeSpan? timeout = null)
    {
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(10));
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
                    throw new WebSocketException(
                        $"服务器关闭连接: {result.CloseStatus} {result.CloseStatusDescription}");
                }
                ms.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);
        }
        catch (OperationCanceledException)
        {
            return null; // 超时无消息——调用方按场景自行判定
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }
}
