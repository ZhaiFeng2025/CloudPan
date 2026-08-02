using System.Security.AccessControl;
using System.Security.Principal;

namespace CloudPan.Server.Services;

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

    /// <summary>设置文件 ACL，仅限当前用户可读可写。</summary>
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
                FileSystemRights.ReadData | FileSystemRights.WriteData,
                AccessControlType.Allow));
        }
        fileInfo.SetAccessControl(accessControl);
    }
}
