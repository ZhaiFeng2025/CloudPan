using System.Text.RegularExpressions;
using CloudPan.Contract;
using CloudPan.Infrastructure.Storage;
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
            // T-085：加长路径前缀前先消解 .. 段——\\?\ 扩展长度路径会跳过 .. 归一化，
            // 保留 .. 会在 OS 打开文件时再解析跳转，形成越界隐蔽通道。
            return @"\\?\" + Path.GetFullPath(path);
        }
        // UNC 路径（\\server\share）转换为 \\?\UNC\ 格式
        if (path.StartsWith(@"\\"))
        {
            return @"\\?\UNC\" + Path.GetFullPath(path)[2..];
        }

        return path;
    }

    /// <summary>相对路径 → 同步根下的绝对本地路径（含长路径前缀；T-085 起强制经越界校验）。</summary>
    public static string ToLocalPath(string syncRoot, string relativePath)
    {
        string? error = LocalPathValidator.Validate(syncRoot, relativePath);
        if (error != null)
        {
            throw new ArgumentException($"拒绝越界相对路径（{error}）: {relativePath}", nameof(relativePath));
        }

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

    /// <summary>
    /// 客户端路径安全统一防线（T-085）：与服务器 FileStorageService.ValidatePath 对等（纵深防御两端闭合）。
    /// 所有『服务端下发相对路径 → 同步根落盘路径』转换必须经 SyncPath.ToLocalPath，其中先经本校验：
    /// 相对路径经 Path.GetFullPath 消解 .. 后仍必须落在 syncRoot 之内，否则拒绝落盘（防目录穿越）。
    /// </summary>
    public static class LocalPathValidator
    {
        /// <summary>
        /// 验证相对路径经 Path.GetFullPath 后仍以 syncRoot 为前缀（在同步根内）。
        /// 返回 null 表示合法，否则返回错误信息。
        /// </summary>
        public static string? Validate(string syncRoot, string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                return "路径不能为空";
            }

            if (relativePath.Contains('\0'))
            {
                return "路径包含空字符";
            }

            try
            {
                // 剥离 \\?\ 前缀后再 GetFullPath：扩展长度路径不会归一化 .. 段，
                // 带前缀直接校验会被 \\?\root\a\..\..\evil 这类路径绕过前缀检查（越界隐蔽通道）
                string cleanRoot = syncRoot.StartsWith(@"\\?\") ? syncRoot[4..] : syncRoot;
                string normalizedRel = relativePath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
                string absolutePath = Path.GetFullPath(Path.Combine(cleanRoot, normalizedRel));
                string rootPrefix = Path.GetFullPath(cleanRoot);
                if (!rootPrefix.EndsWith(Path.DirectorySeparatorChar))
                {
                    rootPrefix += Path.DirectorySeparatorChar;
                }

                if (!absolutePath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    return "路径越界";
                }

                return null; // 合法
            }
            catch (Exception ex)
            {
                // 任意不可解析路径（非法字符等）一律拒绝——防御不可信输入，不抛给调用方
                return $"路径无效: {ex.Message}";
            }
        }
    }
}
