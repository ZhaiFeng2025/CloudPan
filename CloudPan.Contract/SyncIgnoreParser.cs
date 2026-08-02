using System.Text.RegularExpressions;

namespace CloudPan.Shared;

/// <summary>
/// .syncignore 文件解析器。
/// 支持 *（单层通配）、**（递归通配）、?（单字符）、# 注释。
/// 规则与 shared-spec.json config._comments.syncignoreFormat 对齐。
/// </summary>
public static class SyncIgnoreParser
{
    /// <summary>内置默认忽略规则（始终生效）。</summary>
    private static readonly string[] BuiltinPatterns =
    [
        ".cloudpan",     // 元数据目录
        "**/.cloudpan/**",
        "*.tmp",         // 临时文件
        "~*",            // Office 临时文件
        "**/~*",
        "**/.git/**",    // Git 仓库
        "**/node_modules/**"
    ];

    /// <summary>
    /// 从 .syncignore 文件加载忽略规则（合并内置规则）。
    /// 文件不存在则仅返回内置规则。
    /// </summary>
    public static List<Regex> LoadFromSyncRoot(string syncRoot)
    {
        List<Regex> patterns = new List<Regex>();

        // 内置规则
        foreach (string p in BuiltinPatterns)
        {
            patterns.Add(GlobToRegex(p));
        }

        // 用户自定义规则
        string ignoreFile = Path.Combine(syncRoot, ".syncignore");
        if (File.Exists(ignoreFile))
        {
            try
            {
                foreach (string line in File.ReadLines(ignoreFile))
                {
                    string trimmed = line.Trim();
                    if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#'))
                    {
                        continue;
                    }

                    patterns.Add(GlobToRegex(trimmed));
                }
            }
            catch (IOException ex)
            {
                // 杀软锁定或瞬时不可读——使用内置规则继续运行，避免 async void 事件处理器崩溃
                System.Diagnostics.Debug.WriteLine($"[SyncIgnoreParser] 读取 .syncignore 失败: {ex.Message}");
            }
        }

        return patterns;
    }

    /// <summary>
    /// 检查相对路径是否匹配任一忽略规则。
    /// </summary>
    /// <param name="relativePath">相对于同步根的路径，以 / 开头。</param>
    /// <param name="patterns">已加载的 Regex 规则列表。</param>
    public static bool ShouldIgnore(string relativePath, List<Regex> patterns)
    {
        // 规范化：去掉开头的 /，统一使用 / 分隔符
        string normalized = relativePath.TrimStart('/').Replace('\\', '/');
        // 目录匹配需要同时检查带/后缀的版本
        string withSlash = normalized.EndsWith('/') ? normalized : normalized + "/";

        foreach (var regex in patterns)
        {
            if (regex.IsMatch(normalized) || regex.IsMatch(withSlash))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 将 glob 模式转为正则表达式。
    /// </summary>
    private static Regex GlobToRegex(string pattern)
    {
        // 规范化分隔符
        pattern = pattern.Replace('\\', '/').Trim();

        // 判断是否以 / 开头（仅匹配根目录）
        bool rooted = pattern.StartsWith('/');
        if (rooted)
        {
            pattern = pattern[1..];
        }

        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        sb.Append('^');
        if (!rooted)
        {
            // 非根模式可以匹配任意层级前缀
            sb.Append("(?:.*/)?");  // 可选的前缀路径
        }

        int i = 0;
        while (i < pattern.Length)
        {
            switch (pattern[i])
            {
                case '*':
                    if (i + 1 < pattern.Length && pattern[i + 1] == '*')
                    {
                        // ** 递归通配
                        i += 2;
                        // 跳过紧跟的 /
                        if (i < pattern.Length && pattern[i] == '/')
                        {
                            i++;
                        }

                        sb.Append(".*");
                    }
                    else
                    {
                        i++;
                        sb.Append(@"[^/]*"); // * 单层通配
                    }
                    break;

                case '?':
                    i++;
                    sb.Append(@"[^/]"); // 单字符（不含路径分隔符）
                    break;

                case '/':
                    i++;
                    sb.Append(@"\/");
                    break;

                default:
                    // 转义正则特殊字符
                    string special = @"\.+()[]{}^$|";
                    if (special.Contains(pattern[i]))
                    {
                        sb.Append('\\').Append(pattern[i]);
                    }
                    else
                    {
                        sb.Append(pattern[i]);
                    }

                    i++;
                    break;
            }
        }

        sb.Append('$');
        return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.Compiled,
            TimeSpan.FromSeconds(1)); // 1 秒超时防止灾难性回溯挂死线程
    }
}
