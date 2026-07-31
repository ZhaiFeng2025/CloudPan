using System.Text.RegularExpressions;
using Xunit;

namespace CloudPan.Tests.Architecture;

/// <summary>
/// 契约一致性源码扫描测试。
/// 这些测试在 CI 中运行，兜底 Roslyn Analyzer 未覆盖的场景。
/// </summary>
public class ContractSourceScanTests
{
    // 从测试运行目录向上查找解决方案根目录
    private static readonly string ServerDir = FindSolutionDir() is string root
        ? Path.Combine(root, "CloudPan.Server")
        : throw new DirectoryNotFoundException("找不到解决方案根目录");

    private static string? FindSolutionDir()
    {
        string dir = AppContext.BaseDirectory;
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir, "CloudPan.sln")))
            {
                return dir;
            }

            var parent = Directory.GetParent(dir);
            if (parent == null)
            {
                break;
            }

            dir = parent.FullName;
        }
        return null;
    }

    /// <summary>
    /// 验证 Server 源码中不存在手写错误码字符串（如 code = "BAD_REQUEST"）。
    /// </summary>
    [Fact]
    public void Server源码_不含手写错误码字面量()
    {
        List<string> csFiles = Directory.GetFiles(ServerDir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains("Generated") && !f.Contains("obj") && !f.Contains("bin")
                && !f.Contains("ApiErrors.cs")) // ApiErrors.cs 是唯一合法的错误码引用源
            .ToList();

        // 匹配模式: "code": "XXX" 或 code = "XXX"（排除注解和引用）
        Regex pattern = new Regex("""
            code["\s:=]+\s*"[A-Z][A-Z0-9_]{3,}"
            """, RegexOptions.Compiled | RegexOptions.IgnorePatternWhitespace);

        List<string> violations = new List<string>();
        foreach (string? file in csFiles)
        {
            string content = File.ReadAllText(file);
            var matches = pattern.Matches(content);
            foreach (Match match in matches)
            {
                violations.Add($"{Path.GetFileName(file)}: {match.Value.Trim()}");
            }
        }

        Assert.Empty(violations);
    }

    /// <summary>
    /// 验证 Server 源码中不存在原始 JSON 错误体字符串（以 {"error": 开头的字符串字面量）。
    /// </summary>
    [Fact]
    public void Server源码_不含手写JSON错误体()
    {
        List<string> csFiles = Directory.GetFiles(ServerDir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains("Generated") && !f.Contains("obj") && !f.Contains("bin")
                && !f.Contains("ApiErrors.cs")) // ApiErrors.cs 是唯一合法的错误响应工厂
            .ToList();

        // 匹配模式: "error": 后跟 "code": 的原始 JSON（手写错误体）
        Regex pattern = new Regex("""
            "error"\s*:\s*\{\s*"code"\s*:
            """, RegexOptions.Compiled | RegexOptions.IgnorePatternWhitespace);

        List<string> violations = new List<string>();
        foreach (string? file in csFiles)
        {
            string content = File.ReadAllText(file);
            if (pattern.IsMatch(content))
            {
                violations.Add(Path.GetFileName(file));
            }
        }

        if (violations.Count > 0)
        {
            Assert.Fail($"发现手写JSON错误体:\n{string.Join("\n", violations)}");
        }
    }
}
