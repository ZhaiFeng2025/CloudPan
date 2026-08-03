using System.Text.RegularExpressions;
using CloudPan.Contract;
using Microsoft.Extensions.Logging;

namespace CloudPan.Client.Core.Services;

/// <summary>
/// 同步引擎路径/文件工具（T-070 拆分）：路径归一化、相对/本地路径换算、忽略规则与安全删除。
/// 供 SyncEngine 与 SyncQueryService 共用，避免查询侧重复实现路径逻辑。
/// </summary>
internal static class SyncPath
{
    /// <summary>
    /// 为路径添加 \\?\ 前缀以支持长路径（超过 MAX_PATH 260 字符）。
    /// 对支持的所有文件 I/O 操作使用此方法包装路径。
    /// </summary>
    public static string NormalizePath(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return path;
        }
        // 已包含 \\?\ 前缀则跳过
        if (path.StartsWith(@"\\?\"))
        {
            return path;
        }
        // 只对绝对本地路径（如 C:\...）添加前缀
        if (path.Length >= 3 && path[1] == ':' && path[2] == '\\')
        {
            return @"\\?\" + path;
        }
        // UNC 路径（\\server\share）转换为 \\?\UNC\ 格式
        if (path.StartsWith(@"\\"))
        {
            return @"\\?\UNC\" + path[2..];
        }

        return path;
    }

    /// <summary>相对路径 → 同步根下的绝对本地路径（含长路径前缀）。</summary>
    public static string ToLocalPath(string syncRoot, string relativePath)
    {
        string path = Path.Combine(syncRoot, relativePath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
        return NormalizePath(path);
    }

    /// <summary>绝对本地路径 → 同步根下的相对路径（"/" 开头）。</summary>
    public static string ToRelativePath(string syncRoot, string fullPath)
    {
        // 去除 \\?\ 前缀（如有），确保与 syncRoot 格式一致
        string cleanFull = fullPath.StartsWith(@"\\?\") ? fullPath[4..] : fullPath;
        string cleanRoot = syncRoot.StartsWith(@"\\?\") ? syncRoot[4..] : syncRoot;
        string relative = Path.GetRelativePath(cleanRoot, cleanFull);
        return "/" + relative.Replace('\\', '/');
    }

    /// <summary>判断绝对本地路径是否命中忽略规则（基于其相对路径）。</summary>
    public static bool ShouldIgnore(string syncRoot, string fullPath, List<Regex> ignorePatterns)
        => SyncIgnoreParser.ShouldIgnore(ToRelativePath(syncRoot, fullPath), ignorePatterns);

    /// <summary>尽力而为地删除本地文件（不抛异常）。</summary>
    public static void SafeDelete(string path, ILogger logger)
    {
        try { File.Delete(NormalizePath(path)); } catch (Exception ex) { logger.LogWarning(ex, "删除文件失败: {Path}", path); }
    }
}
