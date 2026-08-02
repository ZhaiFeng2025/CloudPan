using System.Text.RegularExpressions;
using Xunit;

namespace CloudPan.Tests.Architecture;

/// <summary>
/// 代码质量架构测试：文件行数上限、public 类型 XML 文档注释。
/// </summary>
public class CodeQualityTests
{
    // Phase 0 分层阈值：核心服务/控制器（含 UI 类，见 T-028）允许 ≤ 1000 行，纯数据/工具类 ≤ 500 行
    private const int MaxServiceControllerLines = 1000;
    private const int MaxOtherLines = 500;

    // 从测试运行目录向上查找解决方案根目录（与 ContractSourceScanTests 相同逻辑）
    private static readonly string[] ProjectDirs = FindProjectDirs();

    private static string[] FindProjectDirs()
    {
        string root = FindSolutionDir()
            ?? throw new DirectoryNotFoundException("找不到解决方案根目录");
        return new[]
        {
            "CloudPan.Server.Host", "CloudPan.Server.Core", "CloudPan.Server.UI",
            "CloudPan.Client.Core", "CloudPan.Client.UI",
            "CloudPan.Contract", "CloudPan.Infrastructure"
        }.Select(name => Path.Combine(root, name)).ToArray();
    }

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

    // 排除生成代码与构建产物目录（Generated/obj/bin/Analyzer 不参与质量门禁；UI 层自 T-028 起纳入门禁）
    private static bool IsExcluded(string filePath)
    {
        string[] segments = filePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments.Any(s => s is "Generated" or "obj" or "bin" or "Analyzer");
    }

    private static List<string> GetSourceFiles() =>
        ProjectDirs
            .SelectMany(dir => Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories))
            .Where(f => !IsExcluded(f))
            .ToList();

    /// <summary>
    /// 验证非生成源码文件行数在合理范围内（UI 层自 T-028 起纳入门禁）。
    /// Phase 0 分层阈值：核心服务/控制器（含 UI 类）≤ 1000 行，其他文件 ≤ 500 行。
    /// </summary>
    [Fact]
    public void 所有非生成文件_小于行数上限()
    {
        // 服务/控制器/UI 类文件（Phase 0 允许 ≤ 1000 行）
        var serviceControllerFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "FilesController.cs", "Program.cs", "ApiClient.cs",
            "SetupForm.cs", "MainWindow.cs", "SettingsForm.cs", "ServerInstaller.cs",
            "ServerWindow.cs", "TrayAppContext.cs", "ServerTrayApp.cs",
            "FileBrowserView.cs",
            "WebSocketClient.cs", "WebSocketHandler.cs",
        };

        List<string> violations = new List<string>();
        foreach (string file in GetSourceFiles())
        {
            int lineCount = File.ReadLines(file).Count();
            string fileName = Path.GetFileName(file);
            int limit = serviceControllerFiles.Contains(fileName) ? MaxServiceControllerLines
                : MaxOtherLines;

            if (lineCount > limit)
            {
                violations.Add($"{fileName}: {lineCount} 行 (上限 {limit})");
            }
        }

        if (violations.Count > 0)
        {
            Assert.Fail($"发现超过行数上限的源码文件:\n{string.Join("\n", violations)}");
        }
    }

    /// <summary>
    /// 验证所有 public 类型（class/record/interface/enum）均有 XML 文档注释。
    /// </summary>
    [Fact]
    public void 所有public类型_有XML文档注释()
    {
        List<string> violations = new List<string>();
        foreach (string file in GetSourceFiles())
        {
            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                if (!TypeDeclarationPattern.IsMatch(lines[i]))
                {
                    continue;
                }

                if (HasXmlDoc(lines, i))
                {
                    continue;
                }

                violations.Add($"{Path.GetFileName(file)}:{i + 1}: {lines[i].Trim()}");
            }
        }

        if (violations.Count > 0)
        {
            Assert.Fail($"发现缺少 XML 文档注释的 public 类型:\n{string.Join("\n", violations)}");
        }
    }

    // public 类型声明：class/record/interface/enum（可带 sealed/abstract/static/partial/readonly 修饰符）
    private static readonly Regex TypeDeclarationPattern = new(
        @"^\s*public\s+(?:sealed\s+|abstract\s+|static\s+|partial\s+|readonly\s+)*(class|record|interface|enum)\s+[A-Za-z0-9_]+",
        RegexOptions.Compiled);

    // 从声明行向上查找：/// 即视为有文档；空行/特性/普通注释/预处理指令可跳过；
    // 遇到其他代码行说明声明前没有文档注释。
    private static bool HasXmlDoc(string[] lines, int index)
    {
        for (int i = index - 1; i >= 0; i--)
        {
            string text = lines[i].Trim();
            if (text.StartsWith("///"))
            {
                return true;
            }

            if (text.Length == 0 || text.StartsWith("//") || text.StartsWith("[") || text.StartsWith("#"))
            {
                continue;
            }

            return false;
        }
        return false;
    }
}
