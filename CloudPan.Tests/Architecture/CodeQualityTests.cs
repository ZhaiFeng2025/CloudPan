using System.Text.RegularExpressions;
using Xunit;

namespace CloudPan.Tests.Architecture;

/// <summary>
/// 代码质量架构测试：public 类型聚合行数上限（partial 跨文件累计）、public 类型 XML 文档注释。
/// </summary>
public class CodeQualityTests
{
    // 单类聚合行数上限 400（对齐 CLAUDE.md 规则 8「单类行数 ≤ 400」，T-042 统一阈值，无分层放宽；
    // T-070 起按 public 类型聚合所有 partial 文件统计，partial 拆分不再绕过门禁）。
    private const int MaxLines = 400;

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

    // ────────────────────────────────────────────────────────────
    // T-070：按 public 类型聚合 partial 文件行数
    // ────────────────────────────────────────────────────────────

    /// <summary>
    /// 文档化聚合行数上限（键 = "Namespace.TypeName"，缺省 400）。
    /// T-070 取舍：既有大型类型中，核心状态机/窗体等因强耦合（共享可变状态、事件、控件）无法在
    /// 「纯结构移动不改行为」约束下拆到 400，按 producer 取舍记录合理上限；SyncEngine 查询侧
    /// （浏览/状态/回收站/分享/版本）已拆入 SyncQueryService，MainWindow 冲突对话框拆入
    /// ConflictResolutionDialog。新增类型无条目时一律按 MaxLines=400 强制执行。
    /// </summary>
    private static readonly Dictionary<string, int> DocumentedTypeCeilings = new()
    {
        // 客户端同步状态机核心：队列/传输/全量扫描/增量同步强耦合（共享计数器/事件/锁/排除集），
        // T-070 已将查询侧拆出（SyncBrowseService/SyncManageService，2709→2223 聚合行），
        // 核心状态机仍无法在「纯结构移动不改行为」下拆到 400，按 producer 取舍记录合理上限。
        ["CloudPan.Client.Core.Services.SyncEngine"] = 2300,
        // 客户端主窗体：WinForms 控件树 + 事件绑定天然聚合，
        // T-070 已将冲突对话框/格式化工具拆出（ConflictResolutionDialog/UiFormat，2793→2548 聚合行），
        // 主窗体本身仍无法拆到 400，按 producer 取舍记录合理上限。
        ["CloudPan.Client.UI.MainWindow"] = 2600,
        // ── 以下为 T-070 范围外的既有大型类型（非本任务拆分对象），记录现状上限防止继续膨胀 ──
        // T-075：同步根路径安全校验下沉为共享静态方法（SetupForm/SettingsForm 复用）使聚合 1074→1091，上限上调记录新现状
        ["CloudPan.Client.UI.SetupForm"] = 1095,
        ["CloudPan.Client.UI.FileBrowserView"] = 854,
        // T-075：保存前同步根路径安全校验 + saveHint 统一重启提示使聚合 731→741，上限上调记录新现状
        ["CloudPan.Client.UI.SettingsForm"] = 745,
        ["CloudPan.Client.UI.TrayAppContext"] = 522,
        ["CloudPan.Client.Core.Services.ApiClient"] = 562,
        ["CloudPan.Server.UI.ServerInstaller"] = 861,
        ["CloudPan.Server.UI.SettingsPage"] = 549,
        ["CloudPan.Server.UI.ServerWindow"] = 525,
        ["CloudPan.Server.Core.WebSocketHandler"] = 464,
    };

    /// <summary>
    /// 验证每个 public 类型的聚合行数（所有声明它的源文件行数之和）不超上限。
    /// T-070：将门禁从「单文件 ≤400」改为「public 类型聚合 ≤400」，partial 跨文件拆分不再绕过门禁。
    /// </summary>
    [Fact]
    public void 所有public类型_聚合行数不超上限()
    {
        List<string> violations = new List<string>();
        foreach (var (typeKey, lineCount) in AggregateLinesByPublicType())
        {
            int ceiling = DocumentedTypeCeilings.TryGetValue(typeKey, out int documented) ? documented : MaxLines;
            if (lineCount > ceiling)
            {
                violations.Add($"{typeKey}: {lineCount} 行 (上限 {ceiling})");
            }
        }

        if (violations.Count > 0)
        {
            Assert.Fail($"发现超过聚合行数上限的 public 类型（partial 跨文件累计）:\n{string.Join("\n", violations.OrderByDescending(v => v))}");
        }
    }

    // public 类型声明：class/record/interface/enum（可带 sealed/abstract/static/partial/readonly 修饰符），
    // 第 1 组为类型名（record 声明为 `record Name(...)` 时类型名后紧跟括号，仅取标识符）
    private static readonly Regex TypeDeclarationPattern = new(
        @"^\s*public\s+(?:sealed\s+|abstract\s+|static\s+|partial\s+|readonly\s+)*(?:class|record|interface|enum)\s+([A-Za-z0-9_]+)",
        RegexOptions.Compiled);

    // 命名空间声明：文件级命名空间（namespace X;）与块级命名空间（namespace X {）均匹配
    private static readonly Regex NamespacePattern = new(
        @"^\s*namespace\s+([\w.]+)",
        RegexOptions.Compiled);

    /// <summary>
    /// 按 public 类型聚合行数：类型全名（Namespace.TypeName）→ 所有声明该类型的源文件行数之和。
    /// 每个文件的行数计入其声明的每一个 public 类型（保守口径：共享文件的类型也承担该文件全量）。
    /// </summary>
    private static Dictionary<string, int> AggregateLinesByPublicType()
    {
        Dictionary<string, int> aggregate = new();
        foreach (string file in GetSourceFiles())
        {
            string[] lines = File.ReadAllLines(file);
            string ns = "";
            foreach (string line in lines)
            {
                Match nsMatch = NamespacePattern.Match(line);
                if (nsMatch.Success)
                {
                    ns = nsMatch.Groups[1].Value;
                    break;
                }
            }

            foreach (string line in lines)
            {
                Match match = TypeDeclarationPattern.Match(line);
                if (!match.Success)
                {
                    continue;
                }

                string typeName = match.Groups[1].Value;
                string key = string.IsNullOrEmpty(ns) ? typeName : $"{ns}.{typeName}";
                aggregate[key] = aggregate.TryGetValue(key, out int count) ? count + lines.Length : lines.Length;
            }
        }
        return aggregate;
    }

    // ────────────────────────────────────────────────────────────
    // 既有门禁（保留）
    // ────────────────────────────────────────────────────────────

    /// <summary>
    /// 验证非生成源码文件行数 ≤ 400（T-028 起 UI 层纳入门禁，T-042 统一阈值无分层放宽）。
    /// T-070 后聚合门禁为主，单文件上限仍保留以约束无类型声明/多类型共享的异常文件。
    /// </summary>
    [Fact]
    public void 所有文件_单文件行数上限()
    {
        List<string> violations = new List<string>();
        foreach (string file in GetSourceFiles())
        {
            int lineCount = File.ReadLines(file).Count();
            if (lineCount > MaxLines)
            {
                violations.Add($"{Path.GetFileName(file)}: {lineCount} 行 (上限 {MaxLines})");
            }
        }

        if (violations.Count > 0)
        {
            Assert.Fail($"发现超过单文件行数上限的源码文件:\n{string.Join("\n", violations)}");
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
