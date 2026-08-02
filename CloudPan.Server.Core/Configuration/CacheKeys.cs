namespace CloudPan.Server.Services;

/// <summary>
/// 内存缓存键常量。Token 哈希缓存由 TokenAuthMiddleware 与 WebSocketHandler 共享，
/// Token 轮换后须 Remove 此键立即失效（否则旧 Token 5 分钟内仍有效）。
/// </summary>
public static class CacheKeys
{
    public const string TokenHash = "token_hash_cache";
}
