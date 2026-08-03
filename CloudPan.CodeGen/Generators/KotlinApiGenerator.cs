using System.Text;

namespace CloudPan.CodeGen.Generators;

/// <summary>
/// 从 shared-spec.json → api.endpoints 生成 Android Retrofit 路由常量（SpecRoutes.g.kt）。
/// 与 C# 侧 SpecRoutes.g.cs（ApiClientGenerator）同源于 spec api.endpoints：
/// Android Retrofit 注解路径去掉前导 "/"（相对路径，Retrofit 与 baseUrl 拼接），
/// C# 侧保留 "/api/..."（ASP.NET 路由模板）。路径单一事实来源为契约。
/// 纳入 --verify：spec 改端点后生成内容变化即被检出，重跑 CodeGen 后 CloudPanApi.kt 全链路生效。
///
/// 渐进项：CloudPanApi Retrofit interface 方法签名（@Query/@Body/@Part 参数绑定）因 spec
/// 无结构化参数段，本次保留手工方法定义（CloudPanApi.kt），仅路由注解改引用本常量。
/// </summary>
public static class KotlinApiGenerator
{
    public static string Generate(SpecDocument spec)
    {
        StringBuilder sb = new StringBuilder();
        sb.AppendLine("// AUTO-GENERATED from shared-spec.json")
          .AppendLine($"// 版本: {spec.Version}  日期: {spec.Date}")
          .AppendLine("// 源: shared-spec.json → api.endpoints（Retrofit 路由常量，与 C# SpecRoutes.g.cs 同源）")
          .AppendLine("// 请勿手工编辑 — 重新生成: dotnet run --project CloudPan.CodeGen")
          .AppendLine();
        sb.AppendLine("package com.cloudpan.android.data");
        sb.AppendLine();
        sb.AppendLine("// Retrofit 路由常量——路径单一事实来源为 shared-spec.json → api.endpoints。");
        sb.AppendLine("// CloudPanApi.kt 的 @GET/@POST/@DELETE 注解引用本常量，禁止硬编码 \"/api/...\" 路由字面量；");
        sb.AppendLine("// 改 spec 端点后重跑 CodeGen 即全链路生效。路径无前导 \"/\"（Retrofit 相对路径，与 baseUrl 拼接）。");
        sb.AppendLine("object SpecRoutes");
        sb.AppendLine("{");

        foreach (var ep in spec.Api.Endpoints)
        {
            string name = ToConstantName(ep.Path);
            string path = ep.Path.TrimStart('/');
            sb.AppendLine("    /**");
            sb.AppendLine($"     * {ep.Description}（{ep.Method.ToUpperInvariant()} {ep.Path}）");
            sb.AppendLine("     */");
            sb.AppendLine($"    const val {name} = \"{path}\"");
            sb.AppendLine();
        }

        sb.AppendLine("}");
        return sb.ToString();
    }

    /// <summary>
    /// 路由路径 → 常量名：/api/files/tree → FilesTree；
    /// /api/shares/{shareId} → SharesByShareId（模板段前加 By）；/pair → Pair；/ws → WebSocket。
    /// 与 ApiClientGenerator.ToConstantName 语义一致，去掉首位 api 段（路由前缀隐含）。
    /// </summary>
    private static string ToConstantName(string path)
    {
        // /ws 特例：WebSocket 端点缩写，映射为 WebSocket（避免生成含义模糊的 Ws）
        if (string.Equals(path, "/ws", StringComparison.OrdinalIgnoreCase))
        {
            return "WebSocket";
        }

        string[] segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length > 0 && string.Equals(segments[0], "api", StringComparison.OrdinalIgnoreCase))
        {
            segments = segments[1..];
        }

        StringBuilder sb = new StringBuilder();
        foreach (string seg in segments)
        {
            if (seg.StartsWith('{') && seg.EndsWith('}'))
            {
                sb.Append("By").Append(ToPascalCase(seg[1..^1]));
            }
            else
            {
                sb.Append(ToPascalCase(seg));
            }
        }
        return sb.ToString();
    }

    /// <summary>段 → PascalCase：cert-fingerprint → CertFingerprint（按 -/_ 分词）。</summary>
    private static string ToPascalCase(string segment)
    {
        StringBuilder sb = new StringBuilder();
        foreach (string word in segment.Split('-', '_'))
        {
            if (word.Length == 0)
            {
                continue;
            }

            sb.Append(char.ToUpperInvariant(word[0]));
            if (word.Length > 1)
            {
                sb.Append(word[1..]);
            }
        }
        return sb.ToString();
    }
}
