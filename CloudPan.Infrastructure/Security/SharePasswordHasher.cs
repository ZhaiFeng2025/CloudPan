using System.Security.Cryptography;

namespace CloudPan.Server.Services;

/// <summary>
/// 分享链接密码哈希器。使用 PBKDF2（带随机盐，防暴力破解），
/// 兼容 v1.1 前创建的旧 SHA256 无盐格式（无 "pbkdf2$" 前缀的视为旧格式）。
/// 格式：pbkdf2$迭代次数$盐Base64$哈希Base64
/// </summary>
public static class SharePasswordHasher
{
    private const int Iterations = 100_000;
    private const int SaltSize = 16;
    private const int HashSize = 32;
    private const string Prefix = "pbkdf2$";

    /// <summary>生成带盐 PBKDF2 哈希。</summary>
    public static string Hash(string password)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(SaltSize);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashSize);
        return $"{Prefix}{Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    /// <summary>校验密码。兼容旧 SHA256 格式（无前缀）。</summary>
    public static bool Verify(string password, string storedHash)
    {
        if (string.IsNullOrEmpty(storedHash))
        {
            return false;
        }

        // 旧格式（v1.1 前）：无前缀 → SHA256 无盐
        if (!storedHash.StartsWith(Prefix, StringComparison.Ordinal))
        {
            string oldHash = Convert.ToHexString(SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(password))).ToLowerInvariant();
            return string.Equals(oldHash, storedHash, StringComparison.OrdinalIgnoreCase);
        }

        string[] parts = storedHash.Split('$');
        if (parts.Length != 4)
        {
            return false;
        }

        try
        {
            int iterations = int.Parse(parts[1]);
            byte[] salt = Convert.FromBase64String(parts[2]);
            byte[] expected = Convert.FromBase64String(parts[3]);
            byte[] actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch
        {
            return false;
        }
    }
}
