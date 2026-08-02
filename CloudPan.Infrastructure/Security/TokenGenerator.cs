using System.Security.Cryptography;

namespace CloudPan.Server.Services;

/// <summary>
/// 家庭共享 Token 生成器（64 位小写十六进制，32 字节 CSPRNG）。
/// 从 DatabaseInitializer 抽出，首次初始化与轮换共用，保证生成规则单一来源。
/// </summary>
public static class TokenGenerator
{
    public static string Generate()
    {
        return Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    }
}
