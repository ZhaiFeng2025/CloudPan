using System.Security.AccessControl;
using System.Security.Principal;

// CP200 抑制：SecretStore 本身是 Token 的唯一授权落盘点（.cloudpan/token.txt + ACL 限权），
// 此处对 token 路径的 File.* 读写是设计意图（受控存取），而非敏感数据散落直写盘。
// 业务代码必须经 SecretStore.ReadToken/WriteToken 存取 Token，不得绕过本服务。
#pragma warning disable CP200

namespace CloudPan.Infrastructure.Security;

/// <summary>
/// Token 机密存取服务。
/// 负责 .cloudpan/token.txt 的写入、读取与删除，并对文件施加 ACL 限制（仅当前用户可读可写）。
/// </summary>
public static class SecretStore
{
    private static string GetTokenPath(string syncRoot) =>
        Path.Combine(syncRoot, ".cloudpan", "token.txt");

    /// <summary>
    /// 写入 Token 到 .cloudpan/token.txt 并设置 ACL（仅当前用户可读可写）。
    /// 先删除旧文件避免 ACL 阻止覆盖。
    /// </summary>
    public static void WriteToken(string token, string syncRoot)
    {
        string tokenFile = GetTokenPath(syncRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(tokenFile)!);

        if (File.Exists(tokenFile))
        {
            // 重置 ACL 为继承模式，确保可删除
            FileInfo fi = new FileInfo(tokenFile);
            var acl = fi.GetAccessControl();
            acl.SetAccessRuleProtection(false, false); // 恢复继承
            fi.SetAccessControl(acl);
            File.Delete(tokenFile);
        }
        File.WriteAllText(tokenFile, token);
        SetTokenFileAcl(tokenFile);
    }

    /// <summary>读取 Token；文件不存在时返回 null。</summary>
    public static string? ReadToken(string syncRoot)
    {
        string tokenFile = GetTokenPath(syncRoot);
        if (!File.Exists(tokenFile))
        {
            return null;
        }

        return File.ReadAllText(tokenFile).Trim();
    }

    /// <summary>删除 Token 文件（先恢复继承模式 ACL，确保可删除）。</summary>
    public static void DeleteTokenFile(string syncRoot)
    {
        string tokenFile = GetTokenPath(syncRoot);
        if (!File.Exists(tokenFile))
        {
            return;
        }

        // 重置 ACL 为继承模式，确保可删除
        FileInfo fi = new FileInfo(tokenFile);
        var acl = fi.GetAccessControl();
        acl.SetAccessRuleProtection(false, false);
        fi.SetAccessControl(acl);
        File.Delete(tokenFile);
    }

    /// <summary>设置文件 ACL，仅限当前用户可完全控制（读写/同步）。禁用继承，其他账户无任何权限。</summary>
    /// <remarks>
    /// 需 FullControl 而非 ReadData|WriteData：同步 I/O 打开文件还要求 ReadAttributes 与 Synchronize，
    /// 缺失会导致同用户写后读取 Access denied（测试暴露，普通进程受限于自身令牌无法绕过）。
    /// 仍限定当前用户 SID，安全语义不变。
    /// </remarks>
    private static void SetTokenFileAcl(string filePath)
    {
        FileInfo fileInfo = new FileInfo(filePath);
        var accessControl = fileInfo.GetAccessControl();
        accessControl.SetAccessRuleProtection(true, false); // 禁用继承，移除继承权限
        var currentUser = WindowsIdentity.GetCurrent().User;
        if (currentUser != null)
        {
            accessControl.AddAccessRule(new FileSystemAccessRule(
                currentUser,
                FileSystemRights.FullControl,
                AccessControlType.Allow));
        }
        fileInfo.SetAccessControl(accessControl);
    }
}
