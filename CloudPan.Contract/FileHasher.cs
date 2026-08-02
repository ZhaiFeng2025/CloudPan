using System.Security.Cryptography;

namespace CloudPan.Contract;

/// <summary>
/// 纯文件哈希共享工具（两端唯一实现）。
/// 全仓 SHA-256 计算的单一来源：服务端 FileStorageService 与客户端 ApiClient/SyncEngine
/// 均调用本工具（F-17/T-017），调整哈希策略只改此处。
/// 输入为规范化绝对路径（Windows 长路径可带 \\?\ 前缀）。
/// </summary>
public static class FileHasher
{
    /// <summary>计算文件 SHA-256（64 字符十六进制）。</summary>
    public static async Task<string> ComputeSha256Async(string absolutePath, CancellationToken ct = default)
    {
        using SHA256 sha = SHA256.Create();
        await using var stream = File.OpenRead(absolutePath);
        byte[] hash = await sha.ComputeHashAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
