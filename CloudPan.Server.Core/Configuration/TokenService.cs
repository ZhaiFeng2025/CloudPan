using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using CloudPan.Contract;
using CloudPan.Infrastructure.Models;
using CloudPan.Infrastructure.Persistence;
using CloudPan.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace CloudPan.Server.Core;

/// <summary>
/// Token 轮换 + 认证 + 设备注册服务（F-25/T-025 单一事实来源）：
///   - 轮换：token.txt（尽力而为）→ DB token_hash（权威源）→ 缓存失效 → 可选断开连接。
///   - 认证：ValidateTokenAsync 供 HTTP 中间件与 WebSocketHandler 共用，消除双实现分叉。
///   - 设备：EnsureDeviceAsync 统一设备格式校验、自动注册与 LastSeen 维护（含并发竞态收敛）。
/// token_hash 读写统一经 ISettingsService（运行时设置唯一通道，规则 0/T-022）。
/// </summary>
public sealed class TokenService : ITokenService
{
    private static readonly Regex DeviceIdRegex = new("^[a-zA-Z0-9_-]+$", RegexOptions.Compiled);

    private readonly IDbContextFactory<CloudPanDbContext> _dbFactory;
    private readonly ISettingsService _settingsService;
    private readonly string _syncRoot;
    private readonly IMemoryCache _cache;
    private readonly ILogger<TokenService> _logger;

    public TokenService(
        IDbContextFactory<CloudPanDbContext> dbFactory,
        ISettingsService settingsService,
        string syncRoot,
        IMemoryCache cache,
        ILogger<TokenService> logger)
    {
        _dbFactory = dbFactory;
        _settingsService = settingsService;
        _syncRoot = syncRoot;
        _cache = cache;
        _logger = logger;
    }

    /// <inheritdoc />
    public event Func<string, Task>? TokenRotated;

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
        //    经 TokenRotated 事件通知订阅者（TokenRotationDisconnector 启动时订阅执行 DisconnectAllAsync），
        //    消除服务定位器延迟解析与 TokenService ⇄ WebSocketHandler 构造期循环依赖（T-072）
        if (disconnectAllClients)
        {
            await RaiseTokenRotatedAsync("token rotated");
        }

        return newToken;
    }

    /// <summary>触发 TokenRotated 事件。multicast delegate 逐个 await 保证所有订阅者完成；任一失败向上抛出（与旧延迟解析语义一致）。</summary>
    private async Task RaiseTokenRotatedAsync(string reason)
    {
        var handler = TokenRotated;
        if (handler == null)
        {
            return;
        }

        foreach (Func<string, Task> subscriber in handler.GetInvocationList())
        {
            await subscriber(reason);
        }
    }

    public Task<string?> GetCurrentTokenAsync()
    {
        // 明文只能来自 token.txt（DB 存 SHA-256 哈希，不可逆）。文件缺失返回 null 由 UI 提示。
        return Task.FromResult(SecretStore.ReadToken(_syncRoot));
    }

    /// <inheritdoc />
    public async Task<TokenValidationResult> ValidateTokenAsync(string token)
    {
        string tokenHash = ComputeSha256(token);
        string? storedHash = await _cache.GetOrCreateAsync(CacheKeys.TokenHash, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5);
            return await _settingsService.GetAsync(SpecSettings.Keys.TokenHash);
        });

        if (storedHash == null)
        {
            return TokenValidationResult.NotInitialized;
        }

        // 十六进制哈希统一 lowercase，OrdinalIgnoreCase 兼容大小写写入
        return string.Equals(tokenHash, storedHash, StringComparison.OrdinalIgnoreCase)
            ? TokenValidationResult.Valid
            : TokenValidationResult.Invalid;
    }

    /// <inheritdoc />
    public async Task<bool> EnsureDeviceAsync(string deviceId, bool? online = null)
    {
        // 格式校验：HTTP 与 WS 共用，防止越界/异常设备 ID（长度 1-64，仅字母/数字/下划线/短横）
        if (string.IsNullOrEmpty(deviceId) || deviceId.Length > 64 || !DeviceIdRegex.IsMatch(deviceId))
        {
            return false;
        }

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var device = await db.Devices.FindAsync(deviceId);
            if (device == null)
            {
                // 自动注册未知设备。Online 由调用方指定：HTTP 传 null→0（HTTP 请求不表示实时在线），
                // WebSocket 传 true/false（连接/断开维护在线状态）
                db.Devices.Add(new Device
                {
                    Id = deviceId,
                    Name = $"设备-{deviceId[..Math.Min(8, deviceId.Length)]}",
                    Person = null,
                    LastSeen = DateTime.UtcNow.ToString("O"),
                    Online = online == true ? 1 : 0,
                    RegisteredAt = DateTime.UtcNow.ToString("O")
                });
            }
            else
            {
                device.LastSeen = DateTime.UtcNow.ToString("O");
                if (online != null)
                {
                    // Online 由 WebSocket 连接/断开管理，HTTP 请求不更新
                    device.Online = online.Value ? 1 : 0;
                }
            }
            await db.SaveChangesAsync();
            return true;
        }
        catch (DbUpdateException ex)
        {
            // 仅唯一约束冲突（并发竞态：另一请求已注册该设备）可重试；
            // 其他约束违反（外键、非空等）不可重试，直接抛出。
            if (!IsUniqueConstraintViolation(ex))
            {
                throw;
            }

            // 并发竞态：另一请求已先行注册该设备。
            // 关键：当前 db 仍跟踪 Add 失败的实体（状态=Added），FindAsync 会优先返回
            // 变更追踪器中的该失败实体而非数据库中的真值，导致二次 INSERT 冲突。
            // 必须使用全新的 DbContext 执行重试查询（CLAUDE.md 7.3）。
            _logger.LogWarning("设备 {DeviceId} 注册并发冲突（正常竞态条件），使用新 DbContext 查询", deviceId);
            await using var freshDb = await _dbFactory.CreateDbContextAsync();
            var freshDevice = await freshDb.Devices.FindAsync(deviceId);
            if (freshDevice != null)
            {
                freshDevice.LastSeen = DateTime.UtcNow.ToString("O");
                if (online != null)
                {
                    freshDevice.Online = online.Value ? 1 : 0;
                }
                await freshDb.SaveChangesAsync();
            }
            return true;
        }
    }

    /// <summary>判断 DbUpdateException 是否由唯一约束/主键冲突触发（可重试），而非外键/非空等不可重试约束。</summary>
    private static bool IsUniqueConstraintViolation(DbUpdateException ex)
    {
        // SQLite 错误码 19 = SQLITE_CONSTRAINT（含 UNIQUE 和 PRIMARY KEY）
        // 在 Microsoft.Data.Sqlite 中内部异常包含 "UNIQUE constraint failed" 或 SQLite 错误码 19
        var inner = ex.InnerException;
        while (inner != null)
        {
            string msg = inner.Message;
            if (msg.Contains("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("SQLITE_CONSTRAINT", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("constraint failed", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            inner = inner.InnerException;
        }
        return false;
    }

    /// <summary>计算 SHA-256（64 字符小写十六进制）。</summary>
    private static string ComputeSha256(string input)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
