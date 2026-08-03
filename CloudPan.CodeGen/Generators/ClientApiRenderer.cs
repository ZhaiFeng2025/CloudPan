using System.Text;

namespace CloudPan.CodeGen.Generators;

/// <summary>
/// ApiClientGenerator 的 C# 客户端方法渲染辅助（T-086 拆分，保持 ApiClientGenerator 类 ≤ 400 行，规则 8.8）。
/// 从 shared-spec.json → api.endpoints[].clientMethod 渲染 IApiClient 接口签名与 ApiClient 方法骨架文本。
/// </summary>
internal static class ClientApiRenderer
{
    /// <summary>方法体分派：query / json-body / delete 三种可生成绑定；其余抛未实现。</summary>
    internal static void AppendMethodBody(StringBuilder sb, ClientMethodDef m, string route)
    {
        switch (m.Kind ?? "query")
        {
            case "query":
                AppendQueryBody(sb, m, route);
                break;
            case "json-body":
                AppendJsonBody(sb, m, route);
                break;
            case "delete":
                AppendDeleteBody(sb, m, route);
                break;
            default:
                sb.AppendLine($"        throw new System.NotImplementedException($\"方法 {m.Name} 未生成：kind={m.Kind}\");");
                break;
        }
    }

    /// <summary>GET + query 参数：必填/带默认内联到首段，可选无默认条件追加（匹配原手工实现行为）。</summary>
    private static void AppendQueryBody(StringBuilder sb, ClientMethodDef m, string route)
    {
        var query = m.Params.Where(p => p.In == "query" && (p.Csharp ?? true)).ToList();
        if (query.Count == 0)
        {
            sb.AppendLine($"        var response = await _http.GetAsync(SpecRoutes.{route}, ct);");
        }
        else
        {
            var inline = query.Where(p => !(p.Optional ?? false) || p.Default is not null).ToList();
            var cond = query.Where(p => (p.Optional ?? false) && p.Default is null).ToList();
            // 生成 $"{route}?sinceVersion={sinceVersion}&limit={limit}"：值段包花括号（QueryExpr 输出 Uri.EscapeDataString(x) 或裸名）
            string inlineStr = string.Join("&", inline.Select(p => p.WireName + "={" + QueryExpr(p) + "}"));
            sb.AppendLine($"        string url = $\"{{SpecRoutes.{route}}}?{inlineStr}\";");
            foreach (var p in cond)
            {
                if (p.Type == "string")
                {
                    sb.AppendLine($"        if (!string.IsNullOrEmpty({p.Name}))");
                    sb.AppendLine("        {");
                    sb.AppendLine(string.Format("            url += $\"&{0}={{{1}}}\";", p.WireName, $"Uri.EscapeDataString({p.Name})"));
                    sb.AppendLine("        }");
                }
                else
                {
                    sb.AppendLine($"        if ({p.Name} is not null)");
                    sb.AppendLine("        {");
                    sb.AppendLine(string.Format("            url += $\"&{0}={{{1}}}\";", p.WireName, p.Name));
                    sb.AppendLine("        }");
                }
            }
            sb.AppendLine($"        var response = await _http.GetAsync(url, ct);");
        }
        sb.AppendLine("        response.EnsureSuccessStatusCode();");
        AppendDeserialize(sb, m);
    }

    /// <summary>POST + JSON body（请求 DTO 由 body 参数顺序构造）。</summary>
    private static void AppendJsonBody(StringBuilder sb, ClientMethodDef m, string route)
    {
        var body = m.Params.Where(p => p.In == "body" && (p.Csharp ?? true)).ToList();
        string dto = body.FirstOrDefault()?.Dto
            ?? throw new InvalidOperationException($"clientMethod {m.Name} kind=json-body 但无 body 参数");
        string args = string.Join(", ", body.Select(p => p.Name));
        sb.AppendLine($"        var response = await _http.PostAsJsonAsync(SpecRoutes.{route}, new {dto}({args}), JsonOptions, ct);");
        sb.AppendLine("        response.EnsureSuccessStatusCode();");
        AppendDeserialize(sb, m);
    }

    /// <summary>DELETE：可选路径参数替换占位符；404 可选返回字面量（如 RevokeShare → false）。</summary>
    private static void AppendDeleteBody(StringBuilder sb, ClientMethodDef m, string route)
    {
        var pathParams = m.Params.Where(p => p.In == "path" && (p.Csharp ?? true)).ToList();
        string urlExpr;
        if (pathParams.Count > 0)
        {
            var pp = pathParams[0];
            sb.AppendLine($"        string url = SpecRoutes.{route}.Replace(\"{{{pp.WireName}}}\", Uri.EscapeDataString({pp.Name}));");
            urlExpr = "url";
        }
        else
        {
            urlExpr = $"SpecRoutes.{route}";
        }

        sb.AppendLine($"        var response = await _http.DeleteAsync({urlExpr}, ct);");
        if (m.NotFoundReturns is not null)
        {
            sb.AppendLine("        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)");
            sb.AppendLine("        {");
            sb.AppendLine($"            return {m.NotFoundReturns};");
            sb.AppendLine("        }");
        }

        sb.AppendLine("        response.EnsureSuccessStatusCode();");
        if (m.NotFoundReturns is not null)
        {
            sb.AppendLine("        return true;");
        }

        AppendDeserialize(sb, m);
    }

    /// <summary>响应反序列化：void 无返回；unwraps 提取 Data 列表；其余返回响应 DTO。</summary>
    private static void AppendDeserialize(StringBuilder sb, ClientMethodDef m)
    {
        string returns = m.Returns ?? m.Response ?? "void";
        if (returns == "void" || m.NotFoundReturns is not null)
        {
            return;
        }

        if (m.Unwraps is not null)
        {
            sb.AppendLine($"        var result = await response.Content.ReadFromJsonAsync<{m.Response}>(JsonOptions, ct);");
            sb.AppendLine($"        return result?.{m.Unwraps}?.ToList() ?? new List<{ElementType(returns)}>();");
        }
        else
        {
            sb.AppendLine($"        return await response.Content.ReadFromJsonAsync<{m.Response}>(JsonOptions, ct);");
        }
    }

    /// <summary>C# 方法签名（含 Task 返回与参数默认值），接口与类骨架共用。</summary>
    internal static string CsMethodSignature(ClientMethodDef m)
    {
        string ret = CsReturnTask(m);
        string ps = string.Join(", ", m.Params.Where(p => p.Csharp ?? true).Select(CsParam));
        if (m.Progress == true)
        {
            ps = ps.Length > 0 ? ps + ", IProgress<long>? progress = null" : "IProgress<long>? progress = null";
        }
        ps = ps.Length > 0 ? ps + ", CancellationToken ct = default" : "CancellationToken ct = default";
        return $"{ret} {m.Name}({ps})";
    }

    private static string CsReturnTask(ClientMethodDef m)
    {
        string returns = m.Returns ?? m.Response
            ?? throw new InvalidOperationException($"clientMethod {m.Name} 缺 returns/response");
        if (returns == "void")
        {
            return "Task";
        }
        if (m.Unwraps is not null)
        {
            return $"Task<List<{ElementType(returns)}>>";
        }
        if (m.Nullable == true)
        {
            return $"Task<{returns}?>";
        }
        return $"Task<{returns}>";
    }

    private static string CsParam(ClientParamDef p)
    {
        string type = p.Type ?? "string";
        bool optional = p.Optional ?? false;
        if (!optional)
        {
            return $"{type} {p.Name}";
        }
        if (type == "string")
        {
            return $"string? {p.Name} = null";
        }
        if (p.Default is not null)
        {
            return $"{type} {p.Name} = {p.Default}";
        }
        return $"{type}? {p.Name} = null";
    }

    /// <summary>query 参数值表达式：string 转义，其余直出。</summary>
    private static string QueryExpr(ClientParamDef p)
        => p.Type == "string" ? $"Uri.EscapeDataString({p.Name})" : p.Name;

    /// <summary>"List&lt;TrashItem&gt;" → "TrashItem"。</summary>
    private static string ElementType(string listType)
    {
        int lt = listType.IndexOf('<');
        int gt = listType.LastIndexOf('>');
        return lt >= 0 && gt > lt ? listType.Substring(lt + 1, gt - lt - 1) : listType;
    }

    internal static bool IsManualFor(ClientMethodDef m, string language)
        => m.Manual is not null && (m.Manual == language || m.Manual == "both");

    internal static void AppendGeneratedHeader(StringBuilder sb, SpecDocument spec, string source)
    {
        sb.AppendLine("// AUTO-GENERATED from shared-spec.json")
          .AppendLine($"// 版本: {spec.Version}  日期: {spec.Date}")
          .AppendLine($"// 源: {source}")
          .AppendLine("// claude: do not edit this file directly — regenerate with: dotnet run --project CloudPan.CodeGen")
          .AppendLine();
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
    }
}
