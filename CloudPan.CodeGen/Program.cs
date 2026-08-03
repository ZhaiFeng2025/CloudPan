using System.Text.Json;
using CloudPan.CodeGen.Generators;

namespace CloudPan.CodeGen;

/// <summary>
/// CloudPan 契约代码生成器。
/// 读取 shared-spec.json，生成 C# 枚举、DTO、实体、Controller 骨架。
///
/// 用法：
///   dotnet run --project CloudPan.CodeGen              # 生成所有代码
///   dotnet run --project CloudPan.CodeGen -- --verify  # 校验模式：比对生成输出与现有文件
/// </summary>
public static class Program
{
    // 输出目录（相对于解决方案根目录）
    // 契约层生成物 → CloudPan.Contract/Generated（DTO/枚举/清单/错误码/API响应，无 UI/EF 依赖）
    private const string SharedOutputDir = "CloudPan.Contract/Generated";
    // 持久化实体依赖 EF Core 特性（[Index]），归属基础设施层 → CloudPan.Infrastructure/Generated
    private const string ServerOutputDir = "CloudPan.Infrastructure/Generated";
    // 客户端本地 EF 实体（SyncQueue/RemoteSnapshot/SyncCursor）→ CloudPan.Infrastructure/Generated（T-068 随持久化迁入基础设施层）
    private const string ClientOutputDir = "CloudPan.Infrastructure/Generated";
    // Android Kotlin 契约产物 → CloudPan.Android/.../data/Generated（package com.cloudpan.android.data）
    private const string AndroidOutputDir = "CloudPan.Android/app/src/main/java/com/cloudpan/android/data/Generated";

    public static int Main(string[] args)
    {
        bool verifyMode = args.Contains("--verify");

        try
        {
            // 1. 定位 shared-spec.json
            string solutionRoot = FindSolutionRoot();
            string specPath = Path.Combine(solutionRoot, "shared-spec.json");
            if (!File.Exists(specPath))
            {
                Console.Error.WriteLine($"❌ 找不到 shared-spec.json: {specPath}");
                return 1;
            }

            Console.WriteLine($"📄 读取契约: {specPath}");
            string json = File.ReadAllText(specPath);
            var spec = JsonSerializer.Deserialize<SpecDocument>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            if (spec == null)
            {
                Console.Error.WriteLine("❌ 无法解析 shared-spec.json");
                return 1;
            }

            Console.WriteLine($"📋 契约版本: {spec.Version}");

            // 2. 生成代码
            Dictionary<string, (string Dir, string File, string Content)> generators = new Dictionary<string, (string Dir, string File, string Content)>
            {
                ["枚举"]     = (SharedOutputDir, "Enums.g.cs",             EnumGenerator.Generate(spec)),
                ["DTO"]      = (SharedOutputDir, "Dtos.g.cs",              DtoGenerator.Generate(spec)),
                ["实体"]     = (ServerOutputDir, "Entities.g.cs",          EntityGenerator.Generate(spec)),
                ["客户端实体"] = (ClientOutputDir, "ClientEntities.g.cs",   EntityGenerator.GenerateClient(spec)),
                ["清单"]     = (SharedOutputDir, "ContractManifest.g.cs",  ManifestGenerator.Generate(spec)),
                ["错误响应"] = (SharedOutputDir, "ErrorResponse.g.cs",     ErrorResponseGenerator.Generate(spec)),
                ["API响应"]  = (SharedOutputDir, "ApiResponses.g.cs",      ApiResponseGenerator.Generate(spec)),
                ["设置"]     = (SharedOutputDir, "Settings.g.cs",          SettingsGenerator.Generate(spec)),
                ["路由常量"] = (SharedOutputDir, "SpecRoutes.g.cs",        ApiClientGenerator.Generate(spec)),
                // Android Kotlin 契约产物（package com.cloudpan.android.data，纳入 --verify）
                ["Kotlin DTO"] = (AndroidOutputDir, "Dtos.g.kt",           KotlinDtoGenerator.Generate(spec)),
                ["Kotlin路由"] = (AndroidOutputDir, "SpecRoutes.g.kt",     KotlinApiGenerator.Generate(spec)),
                // Controller 骨架仅作参考，实际业务逻辑需手写，不再自动生成
            };

            bool hasChanges = false;

            // 2.5. 版本治理断言（T-082）：changelog 严格单调递增 + 顶层版本对齐最新 changelog。
            // verify 模式强制执行，破坏即 CI 失败（hasChanges → 返回 1）。
            if (verifyMode)
            {
                var (versionOk, versionMessage) = ValidateChangelogVersioning(spec);
                if (!versionOk)
                {
                    Console.WriteLine($"❌ 版本治理: {versionMessage}");
                    hasChanges = true;
                }
                else
                {
                    Console.WriteLine($"✅ 版本治理: {versionMessage}");
                }
            }

            foreach (var (label, (dir, filename, content)) in generators)
            {
                string outputDir = Path.Combine(solutionRoot, dir);
                Directory.CreateDirectory(outputDir);
                string outputPath = Path.Combine(outputDir, filename);

                if (verifyMode)
                {
                    // 校验模式：比对生成内容与现有文件
                    if (!File.Exists(outputPath))
                    {
                        Console.WriteLine($"❌ {label}: 文件不存在 — {outputPath}");
                        hasChanges = true;
                        continue;
                    }

                    string existing = File.ReadAllText(outputPath);
                    if (existing != content)
                    {
                        Console.WriteLine($"❌ {label}: 生成内容与现有文件不一致 — {outputPath}");
                        Console.WriteLine($"   提示: 运行 'dotnet run --project CloudPan.CodeGen' 重新生成");
                        hasChanges = true;
                    }
                    else
                    {
                        Console.WriteLine($"✅ {label}: 一致");
                    }
                }
                else
                {
                    // 生成模式
                    string? previousContent = File.Exists(outputPath) ? File.ReadAllText(outputPath) : null;
                    if (previousContent == content)
                    {
                        Console.WriteLine($"⏭️  {label}: 无变更 — {outputPath}");
                    }
                    else
                    {
                        File.WriteAllText(outputPath, content);
                        Console.WriteLine($"✅ {label}: 已生成 — {outputPath}");
                        hasChanges = true;
                    }
                }
            }

            // 3. 规则 0：客户端持久化实体必须从契约生成，禁止手工翻译回归（T-062/T-068）。
            // T-068 实体随持久化迁入 CloudPan.Infrastructure（Generated/ 为合法生成物）；扫描其余目录防手工定义回归。
            if (verifyMode)
            {
                string infraScanDir = Path.Combine(solutionRoot, "CloudPan.Infrastructure");
                string[] manualEntityPatterns = { "public class SyncQueueItem", "public class RemoteSnapshot", "public class SyncCursorState" };
                string infraContent = Directory.Exists(infraScanDir)
                    ? string.Concat(Directory.EnumerateFiles(infraScanDir, "*.cs", SearchOption.AllDirectories)
                        .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}Generated{Path.DirectorySeparatorChar}")
                                 && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                                 && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                        .Select(f => File.ReadAllText(f)))
                    : "";
                string? matched = manualEntityPatterns.FirstOrDefault(p => infraContent.Contains(p, StringComparison.Ordinal));
                if (matched != null)
                {
                    Console.WriteLine($"❌ 客户端实体: Infrastructure 非 Generated 目录含手工实体类定义 '{matched}'，应引用 Generated 类型（规则 0）");
                    hasChanges = true;
                }
                else
                {
                    Console.WriteLine("✅ 客户端实体: Infrastructure 非 Generated 目录无手工实体类定义");
                }
            }

            // 4. 如果基础设施项目还不存在，提示实体输出位置（依赖 EF Core 的实体归属 Infrastructure）
            string infraDir = Path.Combine(solutionRoot, "CloudPan.Infrastructure");
            if (!Directory.Exists(infraDir))
            {
                Console.WriteLine();
                Console.WriteLine("⚠️  CloudPan.Infrastructure 项目尚未创建。持久化实体已生成到:");
                Console.WriteLine($"   {Path.Combine(solutionRoot, ServerOutputDir)}");
                Console.WriteLine("   创建 Infrastructure 项目后，将文件移动到项目内即可。");
            }

            if (verifyMode && hasChanges)
            {
                Console.WriteLine();
                Console.WriteLine("❌ 校验失败：生成代码与契约不一致。请重新生成。");
                return 1;
            }

            Console.WriteLine();
            Console.WriteLine(verifyMode ? "✅ 校验通过" : "✅ 代码生成完成");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"❌ 错误: {ex.Message}");
            Console.Error.WriteLine(ex.StackTrace);
            return 1;
        }
    }

    /// <summary>
    /// 版本治理校验（T-082）：
    /// 1) changelog 版本号严格单调递增（旧→新，无重复）；
    /// 2) 顶层 version == 最新 changelog 版本（对齐锚点）。
    /// 返回 (是否通过, 通过/失败消息)。
    /// </summary>
    private static (bool Ok, string Message) ValidateChangelogVersioning(SpecDocument spec)
    {
        if (spec.Changelog is null || spec.Changelog.Count == 0)
        {
            return (false, "shared-spec.json 缺少 _changelog，版本治理无法校验（应至少包含一条版本记录）");
        }

        for (int i = 1; i < spec.Changelog.Count; i++)
        {
            string prev = spec.Changelog[i - 1].Version;
            string curr = spec.Changelog[i].Version;
            if (CompareVersion(curr, prev) <= 0)
            {
                return (false, $"changelog 版本未严格单调递增：{prev} → {curr}（要求新版本 > 旧版本）");
            }
        }

        string latest = spec.Changelog[^1].Version;
        if (!string.Equals(spec.Version, latest, StringComparison.Ordinal))
        {
            return (false, $"顶层 version={spec.Version} 与最新 changelog 版本={latest} 不一致，应同步");
        }

        return (true, $"changelog {spec.Changelog.Count} 条严格单调递增，顶层 version={spec.Version} 与最新条目一致");
    }

    /// <summary>
    /// 语义化版本 x.y.z 比较：a &gt; b 返回正数，相等返回 0，a &lt; b 返回负数。
    /// 段数不足按 0 补齐（如 1.0 == 1.0.0）。
    /// </summary>
    private static int CompareVersion(string a, string b)
    {
        int[] pa = a.Split('.').Select(s => int.TryParse(s, out var v) ? v : 0).ToArray();
        int[] pb = b.Split('.').Select(s => int.TryParse(s, out var v) ? v : 0).ToArray();
        int n = Math.Max(pa.Length, pb.Length);
        for (int i = 0; i < n; i++)
        {
            int x = i < pa.Length ? pa[i] : 0;
            int y = i < pb.Length ? pb[i] : 0;
            if (x != y)
            {
                return x.CompareTo(y);
            }
        }
        return 0;
    }

    /// <summary>
    /// 向上查找包含 shared-spec.json 的解决方案根目录。
    /// </summary>
    private static string FindSolutionRoot()
    {
        // 从程序运行目录开始向上搜索
        string dir = Environment.CurrentDirectory;
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir, "shared-spec.json")))
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
        throw new InvalidOperationException(
            "无法定位解决方案根目录（未找到 shared-spec.json）。" +
            "请从解决方案根目录或其子目录运行此工具。");
    }
}
