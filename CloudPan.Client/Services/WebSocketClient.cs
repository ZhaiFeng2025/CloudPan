using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace CloudPan.Client.Services;

/// <summary>
/// WebSocket 客户端——连接服务端、接收推送、断线重连。
/// </summary>
public class WebSocketClient : IDisposable
{
    private readonly string _wsUrl;
    private readonly string _token;
    private readonly string _deviceId;
    private readonly ILogger<WebSocketClient> _logger;

    private ClientWebSocket? _ws;
    private volatile bool _running;
    private CancellationTokenSource? _cts;

    /// <summary>重连计数器。</summary>
    private volatile int _attempt;
    /// <summary>断连时间戳（UTC），用于计算已离线时长。</summary>
    private DateTime _disconnectedAt;
    /// <summary>重连等待唤醒信号，支持立即重连。</summary>
    private readonly SemaphoreSlim _reconnectWakeup = new(0);

    /// <summary>指数退避重连延迟（ms）。</summary>
    private static readonly int[] ReconnectDelays = { 1000, 2000, 4000, 8000, 16000, 32000, 60000 };

    // 事件
    public event Action<string>? OnFileChanged;
    public event Action<string>? OnFileDeleted;
    public event Action<string, string>? OnFileRenamed;
    public event Action? OnConnected;
    public event Action? OnDisconnected;
    /// <summary>重连进度通知 (attemptNumber, nextDelaySeconds, offlineSeconds)。仅在间隔 >30 秒时触发。</summary>
    public event Action<int, int, int>? OnReconnectProgress;
    /// <summary>永久断开通知——认证连续失败超上限（10次），Token 可能无效需重新配置。</summary>
    public event Action? OnPermanentFailure;

    public WebSocketClient(string serverUrl, string token, string deviceId, ILogger<WebSocketClient> logger)
    {
        // ws:// 替换 http://（开发环境）
        _wsUrl = serverUrl
            .Replace("https://", "wss://")
            .Replace("http://", "ws://")
            .TrimEnd('/') + "/ws";
        _token = token;
        _deviceId = deviceId;
        _logger = logger;
    }

    /// <summary>启动 WebSocket 连接（含断线重连）。</summary>
    public async Task StartAsync(CancellationToken ct)
    {
        _running = true;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _attempt = 0;

        while (_running && !ct.IsCancellationRequested)
        {
            try
            {
                _ws?.Dispose();
                _ws = new ClientWebSocket();
                _ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);

                await _ws.ConnectAsync(new Uri(_wsUrl), ct);
                _logger.LogInformation("WebSocket 已连接到: {Url}", _wsUrl);

                // 发送认证
                string authMsg = JsonSerializer.Serialize(new
                {
                    type = "auth",
                    token = _token,
                    deviceId = _deviceId
                });
                await SendAsync(authMsg, ct);

                // 等待 auth_ok
                byte[] buffer = new byte[4096];
                var result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                string response = Encoding.UTF8.GetString(buffer, 0, result.Count);

                using JsonDocument doc = JsonDocument.Parse(response);
                string? respType = doc.RootElement.TryGetProperty("type", out var t) ? t.GetString() : null;

                if (respType != "auth_ok")
                {
                    string? message = doc.RootElement.TryGetProperty("message", out var m) ? m.GetString() : "未知错误";
                    _attempt++;
                    if (_attempt > 10)
                    {
                        _logger.LogError("WebSocket 认证失败已达 {Max} 次（Token 可能无效）: {Message}，停止重连", 10, message);
                        OnDisconnected?.Invoke();
                        OnPermanentFailure?.Invoke();
                        break; // 永久退出重连循环
                    }
                    _logger.LogError("WebSocket 认证失败 (第{Attempt}次): {Message}，将按退避策略重连", _attempt, message);
                    _disconnectedAt = DateTime.UtcNow;
                    OnDisconnected?.Invoke();
                    continue; // 进入外层退避重连逻辑
                }

                _logger.LogInformation("WebSocket 认证成功");
                OnConnected?.Invoke();
                _attempt = 0; // 重连成功，重置退避

                // 接收循环
                await ReceiveLoopAsync(ct);
                // 服务端主动关闭连接——通知 UI 离线并进入退避重连
                _disconnectedAt = DateTime.UtcNow;
                OnDisconnected?.Invoke();
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _attempt++;
                _disconnectedAt = DateTime.UtcNow;
                _logger.LogWarning("WebSocket 连接异常 (第{Attempt}次): {Message}", _attempt, ex.Message);
                OnDisconnected?.Invoke();
            }

            // 退避重连
            if (_running && !ct.IsCancellationRequested)
            {
                int delayIndex = Math.Clamp(_attempt - 1, 0, ReconnectDelays.Length - 1);
                int delay = ReconnectDelays[delayIndex];

                // 长间隔时报告进度
                if (delay > 30000)
                {
                    int offlineSeconds = (int)(DateTime.UtcNow - _disconnectedAt).TotalSeconds;
                    int nextDelaySeconds = delay / 1000;
                    OnReconnectProgress?.Invoke(_attempt, nextDelaySeconds, offlineSeconds);
                }

                _logger.LogInformation("WebSocket {Delay}ms 后重连 (第{Attempt}次)", delay, _attempt);

                // 等待延迟（支持 ImmediateReconnect 提前唤醒）
                try
                {
                    // 消耗可能残留的唤醒信号，确保本次按正常退避等待
                    while (_reconnectWakeup.Wait(0)) { }

                    await Task.WhenAny(
                        Task.Delay(delay, ct),
                        _reconnectWakeup.WaitAsync(ct));
                }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    public void Stop()
    {
        _running = false;
        _cts?.Cancel();
        try
        {
            using CancellationTokenSource closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            if (_ws?.State == WebSocketState.Open)
            {
                _ws?.CloseAsync(WebSocketCloseStatus.NormalClosure, "client stop", closeTimeout.Token);
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "WebSocket 关闭异常"); }
    }

    /// <summary>重置退避计数器并触发立即重连。可从 UI 线程调用。</summary>
    public void ImmediateReconnect()
    {
        _attempt = 0;
        try { _reconnectWakeup.Release(); } catch (SemaphoreFullException ex) { _logger.LogWarning(ex, "ImmediateReconnect 信号量已满，无需重复释放"); }
    }

    // ============================================================
    // 接收循环
    // ============================================================

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        byte[] buffer = new byte[8192];
        StringBuilder msgBuilder = new StringBuilder();

        while (_running && !ct.IsCancellationRequested && _ws?.State == WebSocketState.Open)
        {
            try
            {
                WebSocketReceiveResult result;
                msgBuilder.Clear();
                int totalBytes = 0;

                do
                {
                    result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        return;
                    }

                    totalBytes += result.Count;
                    if (totalBytes > 1024 * 1024)
                    {
                        _logger.LogError("WebSocket 消息超过 1MB 限制({Size} bytes)，即将关闭连接", totalBytes);
                        await _ws.CloseAsync(WebSocketCloseStatus.MessageTooBig, "message exceeds 1MB limit", ct);
                        OnDisconnected?.Invoke();
                        return;
                    }

                    msgBuilder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                }
                while (!result.EndOfMessage);

                ProcessMessage(msgBuilder.ToString());
            }
            catch (OperationCanceledException) { return; }
            catch (WebSocketException ex) { _logger.LogDebug(ex, "WebSocket 接收循环断开"); return; }
            catch (Exception ex)
            {
                _logger.LogWarning("WebSocket 消息处理异常: {Message}", ex.Message);
            }
        }
    }

    private void ProcessMessage(string json)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string? type = root.TryGetProperty("type", out var t) ? t.GetString() : null;

            switch (type)
            {
                case "ping":
                    _ = SendAsync("""{"type":"pong"}""", CancellationToken.None);
                    break;

                case "file_changed":
                    string? path = root.TryGetProperty("path", out var p) ? p.GetString() : null;
                    if (path != null)
                    {
                        _logger.LogInformation("WS 推送: 文件变更 {Path}", path);
                        OnFileChanged?.Invoke(path);
                    }
                    break;

                case "file_deleted":
                    string? delPath = root.TryGetProperty("path", out var dp) ? dp.GetString() : null;
                    if (delPath != null)
                    {
                        _logger.LogInformation("WS 推送: 文件删除 {Path}", delPath);
                        OnFileDeleted?.Invoke(delPath);
                    }
                    break;

                case "file_renamed":
                    string? newPath = root.TryGetProperty("path", out var np) ? np.GetString() : null;
                    string? oldPath = null;
                    if (root.TryGetProperty("data", out var data) && data.TryGetProperty("oldPath", out var op))
                    {
                        oldPath = op.GetString();
                    }

                    if (newPath != null && oldPath != null)
                    {
                        _logger.LogInformation("WS 推送: 文件重命名 {OldPath} → {NewPath}", oldPath, newPath);
                        OnFileRenamed?.Invoke(oldPath, newPath);
                    }
                    break;
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "ProcessMessage 收到无效 JSON"); }
    }

    // ============================================================
    // 工具方法
    // ============================================================

    private async Task SendAsync(string json, CancellationToken ct)
    {
        if (_ws?.State != WebSocketState.Open)
        {
            return;
        }

        try
        {
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            await _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "WebSocket 发送消息异常"); }
    }

    public void Dispose()
    {
        Stop();
        _ws?.Dispose();
        _cts?.Dispose();
        _reconnectWakeup.Dispose();
        // 清除全部事件订阅，释放订阅者引用（配合订阅方 Dispose 中的 -= 取消订阅，双保险防泄漏）
        OnFileChanged = null;
        OnFileDeleted = null;
        OnFileRenamed = null;
        OnConnected = null;
        OnDisconnected = null;
        OnReconnectProgress = null;
        OnPermanentFailure = null;
    }
}
