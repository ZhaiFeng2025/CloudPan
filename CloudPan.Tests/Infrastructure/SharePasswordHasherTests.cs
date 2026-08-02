using System.Security.Cryptography;
using CloudPan.Server.Services;
using Xunit;

namespace CloudPan.Tests.Infrastructure;

/// <summary>
/// 分享密码哈希器回归测试：PBKDF2 带盐生成与验证、旧 SHA256 格式兼容、非法格式拒绝。
/// 对应修复：分享密码弱哈希（SHA256 无盐）升级为 PBKDF2。
/// </summary>
public class SharePasswordHasherTests
{
    [Fact]
    public void Hash_产生带盐PBKDF2_可验证且随机盐不同()
    {
        string hash1 = SharePasswordHasher.Hash("secret123");
        string hash2 = SharePasswordHasher.Hash("secret123");

        Assert.StartsWith("pbkdf2$", hash1);
        // 随机盐 → 同一密码两次哈希不同
        Assert.NotEqual(hash1, hash2);
        Assert.True(SharePasswordHasher.Verify("secret123", hash1));
        Assert.False(SharePasswordHasher.Verify("wrong", hash1));
    }

    [Fact]
    public void Verify_兼容旧SHA256无盐格式()
    {
        // v1.1 前创建的分享：SHA256 无盐（64 hex 小写），无 pbkdf2$ 前缀
        string oldHash = Convert.ToHexString(
            SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("secret"))).ToLowerInvariant();

        Assert.True(SharePasswordHasher.Verify("secret", oldHash));
        Assert.False(SharePasswordHasher.Verify("wrong", oldHash));
    }

    [Fact]
    public void Verify_非法或空输入返回false()
    {
        Assert.False(SharePasswordHasher.Verify("x", "not-a-hash"));
        Assert.False(SharePasswordHasher.Verify("x", ""));
        Assert.False(SharePasswordHasher.Verify("x", null!));
        // 格式正确但盐/哈希非法 Base64
        Assert.False(SharePasswordHasher.Verify("x", "pbkdf2$100000$!!!$###"));
    }
}
