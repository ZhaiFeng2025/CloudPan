namespace CloudPan.Server.Core;

/// <summary>
/// Token 验证结果。HTTP 中间件与 WebSocket 认证共用（F-25/T-025 消除行为分叉）。
/// </summary>
public enum TokenValidationResult
{
    /// <summary>Token 有效，允许通过认证。</summary>
    Valid,

    /// <summary>Token 无效（与存储哈希不匹配）。</summary>
    Invalid,

    /// <summary>服务尚未配置 token_hash（未初始化），拒绝所有认证请求。</summary>
    NotInitialized
}

/// <summary>
/// 家庭共享 Token 管理服务——认证与设备注册的单一事实来源（F-25/T-025）。
/// 轮换是唯一在线更新 Token 的入口（DatabaseInitializer 仅在首次初始化生成）；
/// Token 校验（ValidateTokenAsync）与设备注册（EnsureDeviceAsync）供 HTTP 中间件与 WebSocketHandler 共用，
/// 消除两侧重复实现导致的认证行为分叉。
/// </summary>
public interface ITokenService
{
    /// <summary>
    /// 轮换 Token：生成新 64-hex → 写 token.txt（尽力而为）→ 更新 DB token_hash（权威源）→ 立即失效缓存。
    /// 可选断开所有已连接设备。返回新 Token 明文（供 UI 展示/赋值）。
    /// DB 写入失败则抛异常——系统停留在旧 Token，无服务中断。
    /// </summary>
    Task<string> RotateAsync(bool disconnectAllClients);

    /// <summary>读取当前 Token 明文（token.txt）。文件缺失返回 null。</summary>
    Task<string?> GetCurrentTokenAsync();

    /// <summary>
    /// 验证 Token（SHA-256 比对 + 5 分钟内存缓存）。HTTP 与 WS 认证共用此方法，保证两路径校验结果一致。
    /// </summary>
    Task<TokenValidationResult> ValidateTokenAsync(string token);

    /// <summary>
    /// 确保设备已注册并更新 LastSeen；可选按 online 参数更新 Online 在线状态
    /// （HTTP 请求传 null 不更新 Online，WebSocket 连接/断开传 true/false）。
    /// deviceId 为空或格式非法（长度 1-64，仅字母/数字/下划线/短横）返回 false；
    /// 并发注册竞态（唯一约束冲突）安全收敛，返回 true。
    /// </summary>
    Task<bool> EnsureDeviceAsync(string deviceId, bool? online = null);

    /// <summary>
    /// Token 轮换事件（T-072）：TokenService 不再直接引用 IWebSocketHandler（消除服务定位器延迟解析与
    /// TokenService ⇄ WebSocketHandler 构造期循环依赖），轮换需断开连接时经此事件通知订阅者。
    /// 参数为断开原因；multicast delegate 逐个 await，订阅者异常向上抛出。
    /// TokenRotationDisconnector（HostedService）启动时订阅并执行 DisconnectAllAsync。
    /// </summary>
    event Func<string, Task>? TokenRotated;
}
