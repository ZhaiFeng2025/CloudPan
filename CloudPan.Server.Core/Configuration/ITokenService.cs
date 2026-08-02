namespace CloudPan.Server.Core;

/// <summary>
/// 家庭共享 Token 管理服务。轮换是唯一在线更新 Token 的入口（DatabaseInitializer 仅在首次初始化生成）。
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
}
