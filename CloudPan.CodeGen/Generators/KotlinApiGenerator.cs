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

        // 同 path 多 HTTP 方法消歧（T-112）：非首个方法常量名追加方法后缀
        // （POST /api/shares → Shares 保持原名，GET /api/shares → SharesGet），与 C# SpecRoutes.g.cs 同规则
        var routeEntries = BuildRouteEntries(spec);

        foreach (var (name, method, path, description) in routeEntries)
        {
            string kPath = path.TrimStart('/');
            sb.AppendLine("    /**");
            sb.AppendLine($"     * {description}（{method} {path}）");
            sb.AppendLine("     */");
            sb.AppendLine($"    const val {name} = \"{kPath}\"");
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

    /// <summary>
    /// 生成 (常量名, HTTP 方法, 路径, 描述) 路由条目列表。
    /// 同 path 多 HTTP 方法时，非首个方法常量名追加方法后缀（如 GET /api/shares → SharesGet），
    /// 首个方法保持原名（POST /api/shares → Shares，向后兼容）。与 C# SpecRoutes.g.cs 同规则（T-112）。
    /// </summary>
    private static List<(string Name, string Method, string Path, string Description)> BuildRouteEntries(SpecDocument spec)
    {
        var entries = new List<(string, string, string, string)>();
        var methodSeq = new Dictionary<string, int>();
        foreach (var ep in spec.Api.Endpoints)
        {
            int seq = methodSeq.GetValueOrDefault(ep.Path, 0);
            methodSeq[ep.Path] = seq + 1;
            string name = seq == 0
                ? ToConstantName(ep.Path)
                : ToConstantName(ep.Path) + MethodSuffix(ep.Method);
            entries.Add((name, ep.Method.ToUpperInvariant(), ep.Path, ep.Description));
        }
        return entries;
    }

    /// <summary>HTTP 方法 → 常量名后缀（GET → Get，POST → Post，DELETE → Delete 等）。</summary>
    private static string MethodSuffix(string method) => method.ToUpperInvariant() switch
    {
        "GET" => "Get",
        "POST" => "Post",
        "PUT" => "Put",
        "DELETE" => "Delete",
        "PATCH" => "Patch",
        _ => method[..1] + method[1..].ToLowerInvariant()
    };

    // ============================================================
    // Retrofit 接口生成（T-086）
    // ============================================================

    /// <summary>
    /// 生成 Android Retrofit 接口（CloudPan.Android/.../data/Generated/CloudPanApi.g.kt）。
    /// 方法签名（@Query/@Body/@Path/@Part 绑定）由 shared-spec.json → api.endpoints[].clientMethod 驱动，
    /// 与 C# ClientApi.g.cs 同源，纳入 --verify：spec 增/改端点后重跑即强制两端签名一致。
    /// 路由注解引用 SpecRoutes 常量，返回类型引用 Dtos.g.kt（均由 shared-spec.json 生成）。
    /// </summary>
    public static string GenerateClientApi(SpecDocument spec)
    {
        StringBuilder sb = new StringBuilder();
        sb.AppendLine("// AUTO-GENERATED from shared-spec.json")
          .AppendLine($"// 版本: {spec.Version}  日期: {spec.Date}")
          .AppendLine("// 源: shared-spec.json → api.endpoints[].clientMethod（Retrofit interface，与 C# ClientApi.g.cs 同源）")
          .AppendLine("// 请勿手工编辑 — 重新生成: dotnet run --project CloudPan.CodeGen")
          .AppendLine();
        sb.AppendLine("package com.cloudpan.android.data");
        sb.AppendLine();
        sb.AppendLine("import okhttp3.MultipartBody");
        sb.AppendLine("import okhttp3.RequestBody");
        sb.AppendLine("import retrofit2.Response");
        sb.AppendLine("import retrofit2.http.*");
        sb.AppendLine();
        sb.AppendLine("/**");
        sb.AppendLine(" * Retrofit HTTP 接口——方法签名由 shared-spec.json → api.endpoints[].clientMethod 生成（T-086）。");
        sb.AppendLine(" * 路由注解引用 SpecRoutes 常量，返回类型引用 Dtos.g.kt；");
        sb.AppendLine(" * 改 spec 端点后重跑 CodeGen --verify 强制 C#/Kotlin 两端接口签名一致，禁止手工翻译回归。");
        sb.AppendLine(" */");
        sb.AppendLine("interface CloudPanApi {");
        foreach (var ep in spec.Api.Endpoints)
        {
            if (ep.ClientMethod is null)
            {
                continue;
            }

            foreach (var m in ep.ClientMethod)
            {
                if (!(m.Kotlin ?? true))
                {
                    continue;
                }

                GenerateKotlinMethod(sb, ep, m);
                sb.AppendLine();
            }
        }
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static void GenerateKotlinMethod(StringBuilder sb, EndpointDef ep, ClientMethodDef m)
    {
        string route = ToConstantName(ep.Path);
        string method = ep.Method.ToUpperInvariant();
        string kotlinName = m.KotlinName ?? ToKotlinMethodName(m.Name);
        string ret = m.KotlinReturns ?? (m.Response is not null ? $"Response<{m.Response}>" : "Response<Unit>");
        sb.AppendLine("    /**");
        sb.AppendLine($"     * {ep.Description}（{method} {ep.Path}）");
        sb.AppendLine("     */");
        if (m.Kind == "multipart")
        {
            sb.AppendLine("    @Multipart");
        }
        sb.AppendLine($"    @{method}(SpecRoutes.{route})");
        sb.AppendLine($"    suspend fun {kotlinName}({KotlinParams(m)}): {ret}");
    }

    /// <summary>
    /// Kotlin 参数表：query/path 逐参数注解；body 折叠为单个 @Body request: {dto}；local 跳过；
    /// part/file 生成 @Part（字段名来自 wireName / MultipartBody.Part）。
    /// </summary>
    private static string KotlinParams(ClientMethodDef m)
    {
        List<string> parts = new List<string>();
        foreach (var p in m.Params)
        {
            if (!(p.Kotlin ?? true) || p.In == "local" || p.In == "body")
            {
                continue; // body 统一折叠到末尾
            }

            string kname = p.KotlinName ?? p.Name;
            string ktype = p.KotlinType ?? MapKotlinType(p.Type ?? "string");
            string annot = p.In switch
            {
                "query" => $"@Query(\"{p.WireName}\")",
                "path" => $"@Path(\"{p.WireName}\")",
                "part" => p.WireName is not null ? $"@Part(\"{p.WireName}\")" : "@Part",
                "file" => "@Part",
                _ => ""
            };
            string def = "";
            if (p.Optional == true)
            {
                if (ktype.EndsWith("?"))
                {
                    def = " = null";
                }
                else if (p.Default is not null)
                {
                    def = $" = {p.Default}";
                }
                else
                {
                    ktype += "?";
                    def = " = null";
                }
            }
            parts.Add($"{annot} {kname}: {ktype}{def}");
        }

        string? bodyDto = m.Params.FirstOrDefault(p => p.In == "body")?.Dto;
        if (bodyDto is not null)
        {
            parts.Add($"@Body request: {bodyDto}");
        }
        return string.Join(", ", parts);
    }

    private static string MapKotlinType(string t) => t switch
    {
        "string" => "String",
        "int" => "Int",
        "long" => "Long",
        "bool" => "Boolean",
        _ => t
    };

    /// <summary>C# 方法名 → Kotlin 方法名：GetFileTreeAsync → getFileTree（小写首字母，去 Async 后缀）。</summary>
    private static string ToKotlinMethodName(string csName)
    {
        string baseName = csName.EndsWith("Async") ? csName[..^"Async".Length] : csName;
        return char.ToLowerInvariant(baseName[0]) + baseName[1..];
    }
}
