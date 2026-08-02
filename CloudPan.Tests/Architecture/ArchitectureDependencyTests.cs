using System.Xml.Linq;
using CloudPan.Server.Host.Hosting;
using Xunit;

namespace CloudPan.Tests.Architecture;

/// <summary>
/// 架构依赖方向硬门禁：每个项目的 ProjectReference 必须 ⊆ 允许的下层项目集。
/// 规则来源：docs/architecture-requirements.md R-A1（Host/UI → Core → Infrastructure → Contract 单向，禁止反向/跳层）。
/// 编译期断言，CI 强制。Analyzer 注入引用（OutputItemType=Analyzer）不产生程序集依赖，不计入。
/// </summary>
public class ArchitectureDependencyTests
{
    /// <summary>分层依赖白名单：项目 → 允许引用的项目（不含自己）。未列出的项目（Tests 等）不受门禁约束。</summary>
    private static readonly Dictionary<string, string[]> AllowedReferences = new(StringComparer.OrdinalIgnoreCase)
    {
        // 契约层：零依赖
        ["CloudPan.Contract"]       = Array.Empty<string>(),
        // 基础设施层：仅依赖契约
        ["CloudPan.Infrastructure"] = new[] { "CloudPan.Contract" },
        // 领域层：仅依赖基础设施/契约
        ["CloudPan.Server.Core"]    = new[] { "CloudPan.Contract", "CloudPan.Infrastructure" },
        ["CloudPan.Client.Core"]    = new[] { "CloudPan.Contract", "CloudPan.Infrastructure" },
        // 宿主层：依赖领域/基础设施/契约。T-015：Host 不再引用 UI（headless 可编译）；
        // UI 实现 Host 定义的 IHostUIBridge 桥契约，依赖方向单向 UI→Host（同级宿主层内可选模块依赖契约提供方）
        ["CloudPan.Server.Host"]    = new[] { "CloudPan.Contract", "CloudPan.Infrastructure", "CloudPan.Server.Core" },
        ["CloudPan.Server.UI"]      = new[] { "CloudPan.Contract", "CloudPan.Infrastructure", "CloudPan.Server.Core", "CloudPan.Server.Host" },
        ["CloudPan.Client.UI"]      = new[] { "CloudPan.Contract", "CloudPan.Infrastructure", "CloudPan.Client.Core" },
        // 工具项目：零依赖
        ["CloudPan.CodeGen"]        = Array.Empty<string>(),
        ["CloudPan.Analyzers"]      = Array.Empty<string>(),
    };

    [Fact]
    public void 依赖方向_严格单向_无违反()
    {
        string root = FindSolutionDir()
            ?? throw new DirectoryNotFoundException("找不到解决方案根目录（CloudPan.sln）");

        List<string> violations = new List<string>();
        foreach (string csproj in Directory.GetFiles(root, "*.csproj", SearchOption.AllDirectories)
                     .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                              && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")))
        {
            string projectName = Path.GetFileNameWithoutExtension(csproj);
            if (!AllowedReferences.TryGetValue(projectName, out string[]? allowed))
            {
                continue; // 未约束项目（Tests/Android）不参与门禁
            }

            HashSet<string> allowedSet = new(allowed, StringComparer.OrdinalIgnoreCase);
            XDocument doc = XDocument.Load(csproj);

            foreach (XElement pr in doc.Descendants("ProjectReference"))
            {
                // 跳过 Analyzer 注入引用（OutputItemType="Analyzer" / ReferenceOutputAssembly="false"），不产生程序集依赖
                if (pr.Attribute("ReferenceOutputAssembly")?.Value == "false")
                {
                    continue;
                }

                string? include = pr.Attribute("Include")?.Value;
                if (include == null)
                {
                    continue;
                }

                string referenced = Path.GetFileNameWithoutExtension(include);
                if (!allowedSet.Contains(referenced))
                {
                    violations.Add($"{projectName} → 引用 {referenced}（超出允许集: [{string.Join(", ", allowed)}]）");
                }
            }
        }

        Assert.True(violations.Count == 0,
            $"架构依赖方向违反（R-A1 单向依赖）:\n{string.Join("\n", violations)}\n" +
            "规则: Host/UI → Core → Infrastructure → Contract，禁止反向/跳层。见 docs/architecture-requirements.md");
    }

    /// <summary>
    /// T-015 / R-Q3 headless 编译门禁：Host 程序集（CloudPan.Server）的元数据引用不得含可选 UI 程序集，
    /// 且 Host csproj 无 UI 项目引用——保证 Host 不携带 UI 程序集即可编译并独立运行（headless）。
    /// </summary>
    [Fact]
    public void Host程序集_不引用UI_headless可编译()
    {
        // 编译产物级门禁：Host 程序集元数据引用不含 UI 程序集（typeof(TrayAppRunner) 取自 Host 程序集）
        var hostAsm = typeof(TrayAppRunner).Assembly;
        Assert.DoesNotContain(
            hostAsm.GetReferencedAssemblies(),
            r => r.Name == "CloudPan.Server.UI");

        // 项目文件级门禁：Host csproj 不含 Server.UI 引用（对齐验收标准 grep 命令）
        string hostCsproj = Path.Combine(
            FindSolutionDir() ?? throw new DirectoryNotFoundException("找不到解决方案根目录（CloudPan.sln）"),
            "CloudPan.Server.Host", "CloudPan.Server.Host.csproj");
        Assert.DoesNotContain("Server.UI", File.ReadAllText(hostCsproj), StringComparison.OrdinalIgnoreCase);
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
}
