using System.Security.Cryptography;
using System.Text;
using CloudPan.Contract;
using CloudPan.Infrastructure.Security;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace CloudPan.Server.Core;

/// <summary>
/// Token 轮换服务。副作用顺序即一致性策略：
///   token.txt（尽力而为） → DB token_hash（权威源） → 缓存失效 → 可选断开连接。
/// 文件失败不阻断（展示用途），DB 失败则抛异常（系统停留旧 Token，无服务中断）。
/// token_hash 写入统一经 ISettingsService（运行时设置唯一通道，规则 0/T-022）。
/// </summary>
public sealed class TokenService : ITokenService
{
    private readonly ISettingsService _settingsService;
    private readonly string _syncRoot;
    private readonly IMemoryCache _cache;
    private readonly IWebSocketHandler _ws;
    private readonly ILogger<TokenService> _logger;

    public TokenService(
        ISettingsService settingsService,
        string syncRoot,
        IMemoryCache cache,
        IWebSocketHandler ws,
        ILogger<TokenService> logger)
    {
        _settingsService = settingsService;
        _syncRoot = syncRoot;
        _cache = cache;
        _ws = ws;
        _logger = logger;
    }

    public async Task<string> RotateAsync(bool disconnectAllClients)
    {
        string newToken = TokenGenerator.Generate();

        // 1. token.txt（尽力而为）：失败仅记录——文件是展示用途，DB 是权威源
        try
        {
            SecretStore.WriteToken(newToken, _syncRoot);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Token 轮换：写入 token.txt 失败（非致命，DB 哈希仍为权威源）");
        }

        // 2. DB token_hash（权威源）经 ISettingsService 写入：失败则抛异常，系统停留在旧 Token
        string tokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(newToken))).ToLowerInvariant();
        await _settingsService.SetStringAsync(SpecSettings.Keys.TokenHash, tokenHash);

        // 3. 立即失效 5 分钟缓存——旧 Token 即刻失效，无需等缓存过期
        _cache.Remove(CacheKeys.TokenHash);

        // 4. 可选：断开所有已连接设备（Token 轮换默认不踢，家庭场景避免全员掉线）
        if (disconnectAllClients)
        {
            await _ws.DisconnectAllAsync("token rotated");
        }

        return newToken;
    }

    public Task<string?> GetCurrentTokenAsync()
    {
        // 明文只能来自 token.txt（DB 存 SHA-256 哈希，不可逆）。文件缺失返回 null 由 UI 提示。
        return Task.FromResult(SecretStore.ReadToken(_syncRoot));
    }
}
