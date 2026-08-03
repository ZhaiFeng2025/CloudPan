using System.Text.RegularExpressions;
using Xunit;

namespace CloudPan.Tests.Architecture;

/// <summary>
/// 代码质量架构测试：public 类型聚合行数上限（partial 跨文件累计）、public 类型 XML 文档注释。
/// </summary>
public class CodeQualityTests
{
    // 单类聚合行数上限 400（对齐 CLAUDE.md 规则 8「单类行数 ≤ 400」，T-042 统一阈值，无分层放宽；
    // T-070 起按 public 类型聚合所有 partial 文件统计，partial 拆分不再绕过门禁；
    // T-081 起豁免表改为过渡期登记并设持续下降约束，见 Exemptions）。
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
    // T-081：豁免表改为「过渡期登记」，设持续下降约束（拆除永久豁免）
    // ────────────────────────────────────────────────────────────

    // 豁免基线：T-070 审查登记的豁免类型总数。门禁断言豁免总数只减不增——
    // 新增类型无豁免必须拆到 ≤400，禁止以登记方式把违规合法化。
    private const int ExemptionBaselineCount = 11;

    // T-070 基线聚合行数：SyncEngine=2223、MainWindow=2548。T-081 门禁断言其聚合行数必须低于
    // 该基线（本任务已拆出 SyncProgressTracker / GlowDot+ProgressBarWithText 达成：2158/2380），
    // 保证豁免上限随批次只降不升，规则 8 对最大类型重新获得约束力。
    private const int SyncEngineT070Baseline = 2223;
    private const int MainWindowT070Baseline = 2548;

    /// <summary>过渡期豁免登记条目：类型 + 当前过渡上限 + 下降目标 + 达 ≤400 截止任务/批次 + 理由。</summary>
    /// <remarks>
    /// 每个豁免必须满足：TargetCeiling &lt; Ceiling（存在下降计划，上限只降不升）、Deadline 非空
    /// （达 ≤400 的截止任务/批次引用）。新增登记必须注释理由与截止任务；类型达到 ≤400 后从表移除。
    /// </remarks>
    private sealed record TypeExemption(
        string TypeKey,        // "Namespace.TypeName"
        int Ceiling,           // 当前过渡上限（> 400，随批次只降不升）
        int TargetCeiling,     // 下降目标（必须 < Ceiling，下一批次里程碑）
        string Deadline,       // 达 ≤400 的截止任务/批次引用
        string Reason);        // 豁免理由（新增登记必填）

    /// <summary>
    /// 过渡期豁免登记（T-081）。既有大型类型因强耦合（共享可变状态/事件/锁/控件树）无法在
    /// 「纯结构移动不改行为」约束下拆到 400，按 producer 取舍登记过渡上限并附持续下降计划；
    /// 新增类型无条目时一律按 MaxLines=400 强制执行。
    /// </summary>
    private static readonly TypeExemption[] Exemptions =
    {
        // 客户端同步状态机核心：队列/传输/全量扫描/增量同步强耦合（共享计数器/事件/锁/排除集）。
        // T-070 拆查询侧（SyncBrowseService/SyncManageService，2709→2223）；T-081 拆出进度跟踪
        // （SyncProgressTracker，聚合 2227→2158，上限 2300→2200）。核心仍 >400，登记过渡上限。
        new("CloudPan.Client.Core.Services.SyncEngine", 2200, 2050, "批次 8：拆出传输/队列逻辑后 ≤400",
            "客户端同步状态机核心：队列/传输/全量扫描/增量同步强耦合，纯结构移动无法拆到 400。"),
        // 客户端主窗体：WinForms 控件树 + 事件绑定天然聚合。
        // T-070 拆冲突对话框/格式化工具（ConflictResolutionDialog/UiFormat，2793→2548）；T-081 提升
        // GlowDot/ProgressBarWithText 为顶层控件类（聚合 2548→2380，上限 2600→2500）。主窗体仍 >400。
        new("CloudPan.Client.UI.MainWindow", 2500, 2350, "批次 8：按视图区域继续拆分后 ≤400",
            "客户端主窗体：WinForms 控件树 + 事件绑定天然聚合，纯结构移动无法拆到 400。"),
        // ── T-070 范围外的既有大型类型（T-081 不拆分），记录现状上限防膨胀并登记下降计划 ──
        new("CloudPan.Client.UI.SetupForm", 1095, 950, "批次 9：同步根路径校验等下沉后 ≤400",
            "T-075：同步根路径安全校验下沉为共享静态方法（SetupForm/SettingsForm 复用）使聚合 1074→1091。"),
        new("CloudPan.Client.UI.FileBrowserView", 854, 700, "批次 9：多选/批量视图拆分后 ≤400",
            "T-070 范围外既有大型类型，记录现状上限防膨胀。"),
        new("CloudPan.Client.UI.SettingsForm", 745, 650, "批次 9：保存前校验下沉后 ≤400",
            "T-075：保存前同步根路径安全校验 + saveHint 统一重启提示使聚合 731→741。"),
        new("CloudPan.Client.UI.TrayAppContext", 522, 460, "批次 9：托盘菜单/逻辑拆分后 ≤400",
            "T-070 范围外既有大型类型，记录现状上限防膨胀。"),
        new("CloudPan.Client.Core.Services.ApiClient", 562, 500, "批次 9：分块上传/端点收敛后 ≤400",
            "T-070 范围外既有大型类型，记录现状上限防膨胀。"),
        new("CloudPan.Server.UI.ServerInstaller", 861, 700, "批次 9：安装步骤拆分后 ≤400",
            "T-070 范围外既有大型类型，记录现状上限防膨胀。"),
        new("CloudPan.Server.UI.SettingsPage", 549, 480, "批次 9：设置页分区块后 ≤400",
            "T-070 范围外既有大型类型，记录现状上限防膨胀。"),
        new("CloudPan.Server.UI.ServerWindow", 525, 460, "批次 9：服务端窗口视图拆分后 ≤400",
            "T-070 范围外既有大型类型，记录现状上限防膨胀。"),
        new("CloudPan.Server.Core.WebSocketHandler", 464, 430, "批次 9：WS 消息分发拆分后 ≤400",
            "T-070 范围外既有大型类型，记录现状上限防膨胀。"),
    };

    /// <summary>豁免登记按类型键索引（供聚合行数门禁查上限）。</summary>
    private static readonly Dictionary<string, TypeExemption> ExemptionLookup =
        Exemptions.ToDictionary(e => e.TypeKey);

    /// <summary>
    /// 验证每个 public 类型的聚合行数（所有声明它的源文件行数之和）不超上限。
    /// T-070：将门禁从「单文件 ≤400」改为「public 类型聚合 ≤400」，partial 跨文件拆分不再绕过门禁；
    /// T-081：豁免上限由过渡期登记 Exemptions 提供，无登记类型一律 MaxLines=400 强制执行。
    /// </summary>
    [Fact]
    public void 所有public类型_聚合行数不超上限()
    {
        List<string> violations = new List<string>();
        foreach (var (typeKey, lineCount) in AggregateLinesByPublicType())
        {
            int ceiling = ExemptionLookup.TryGetValue(typeKey, out TypeExemption? exemption) ? exemption.Ceiling : MaxLines;
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

    /// <summary>
    /// T-081：豁免表持续下降约束——豁免总数只减不增、每项必有下降计划（TargetCeiling &lt; Ceiling）、
    /// 截止任务与理由非空、SyncEngine/MainWindow 聚合行数较 T-070 基线下降。杜绝豁免只增不减、上限只升不降。
    /// </summary>
    [Fact]
    public void 豁免表_持续下降约束()
    {
        List<string> failures = new List<string>();
        Dictionary<string, int> aggregate = AggregateLinesByPublicType();

        // 1) 豁免总数只减不增（基线 11，新增类型须拆到 ≤400 不得豁免）
        if (Exemptions.Length > ExemptionBaselineCount)
        {
            failures.Add($"豁免总数 {Exemptions.Length} 超过基线 {ExemptionBaselineCount}——禁止新增豁免，新类型须拆到 ≤400");
        }

        // 2) 每个豁免必须存在下降计划（TargetCeiling < Ceiling）、达 ≤400 截止任务与理由（新增登记必填）
        foreach (TypeExemption ex in Exemptions)
        {
            if (ex.TargetCeiling >= ex.Ceiling)
            {
                failures.Add($"{ex.TypeKey}: TargetCeiling({ex.TargetCeiling}) 未小于 Ceiling({ex.Ceiling})——每个豁免必须存在下降计划");
            }
            if (string.IsNullOrWhiteSpace(ex.Deadline))
            {
                failures.Add($"{ex.TypeKey}: 缺少达 ≤400 的截止任务/批次引用");
            }
            if (string.IsNullOrWhiteSpace(ex.Reason))
            {
                failures.Add($"{ex.TypeKey}: 缺少豁免理由");
            }
        }

        // 3) SyncEngine/MainWindow 聚合行数较 T-070 基线下降（上限只降不升，防止豁免合法化违规）
        if (aggregate.TryGetValue("CloudPan.Client.Core.Services.SyncEngine", out int syncLines)
            && syncLines >= SyncEngineT070Baseline)
        {
            failures.Add($"SyncEngine 聚合 {syncLines} 行未低于 T-070 基线 {SyncEngineT070Baseline}");
        }
        if (aggregate.TryGetValue("CloudPan.Client.UI.MainWindow", out int mwLines)
            && mwLines >= MainWindowT070Baseline)
        {
            failures.Add($"MainWindow 聚合 {mwLines} 行未低于 T-070 基线 {MainWindowT070Baseline}");
        }

        if (failures.Count > 0)
        {
            Assert.Fail("豁免表持续下降约束被破坏:\n" + string.Join("\n", failures));
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
