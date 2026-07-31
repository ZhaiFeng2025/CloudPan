using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace CloudPan.Analyzers;

/// <summary>shared-spec.json 中注册的一个端点。</summary>
internal sealed class SpecEndpoint
{
    public SpecEndpoint(string method, string path, string auth)
    {
        Method = method;
        NormalizedPath = path;
        Auth = auth;
    }

    /// <summary>HTTP 方法（大写），如 GET。</summary>
    public string Method { get; }

    /// <summary>归一化路径（以 / 开头，模板参数已替换为 {x}）。</summary>
    public string NormalizedPath { get; }

    /// <summary>认证要求：public / token / localhost。</summary>
    public string Auth { get; }
}

/// <summary>
/// 读取 AdditionalFiles 中 shared-spec.json 的端点表（契约唯一事实来源）。
/// 生成器输出为「每行一个端点对象」，键序固定为 method/path/auth/description，按行正则解析；
/// 若生成器改变输出格式，需同步更新 <see cref="EndpointLineRegex"/>。
/// </summary>
internal static class SpecEndpoints
{
    private static readonly Regex EndpointLineRegex = new(
        "\"method\"\\s*:\\s*\"([A-Z]+)\"[^,]*,\\s*\"path\"\\s*:\\s*\"([^\"]+)\"[^,]*,\\s*\"auth\"\\s*:\\s*\"([^\"]+)\"",
        RegexOptions.Compiled);

    /// <summary>spec 路径中的模板参数（{shareId}）归一化为 {x}，与代码侧路由对齐。</summary>
    private static readonly Regex TemplateParamRegex = new(@"\{[^}]*\}", RegexOptions.Compiled);

    private static readonly object SyncRoot = new();
    private static string? _cachedSource;
    private static ImmutableArray<SpecEndpoint> _cache = ImmutableArray<SpecEndpoint>.Empty;

    /// <summary>按来源内容缓存解析结果，避免每个节点重复解析。</summary>
    public static ImmutableArray<SpecEndpoint> Get(AnalyzerOptions options)
    {
        AdditionalText? spec = null;
        foreach (AdditionalText file in options.AdditionalFiles)
        {
            if (System.IO.Path.GetFileName(file.Path) == "shared-spec.json")
            {
                spec = file;
                break;
            }
        }
        if (spec is null)
        {
            return ImmutableArray<SpecEndpoint>.Empty;
        }

        string source = spec.GetText()?.ToString() ?? string.Empty;
        lock (SyncRoot)
        {
            if (_cachedSource != source)
            {
                _cachedSource = source;
                _cache = Parse(source);
            }
            return _cache;
        }
    }

    /// <summary>按方法与归一化路径查找端点（路径不区分大小写）。</summary>
    public static SpecEndpoint? Find(ImmutableArray<SpecEndpoint> endpoints, string method, string normalizedPath)
    {
        foreach (SpecEndpoint endpoint in endpoints)
        {
            if (endpoint.Method == method
                && string.Equals(endpoint.NormalizedPath, normalizedPath, System.StringComparison.OrdinalIgnoreCase))
            {
                return endpoint;
            }
        }
        return null;
    }

    private static ImmutableArray<SpecEndpoint> Parse(string source)
    {
        ImmutableArray<SpecEndpoint>.Builder builder = ImmutableArray.CreateBuilder<SpecEndpoint>();
        foreach (string line in source.Split('\n'))
        {
            Match match = EndpointLineRegex.Match(line);
            if (match.Success)
            {
                // spec 中模板参数保持原始形式（{shareId}），归一化为 {x} 后再比较
                string path = TemplateParamRegex.Replace(match.Groups[2].Value, "{x}");
                builder.Add(new SpecEndpoint(match.Groups[1].Value, path, match.Groups[3].Value));
            }
        }
        return builder.ToImmutable();
    }
}

/// <summary>端点 action 的识别结果：类 [Route] + 方法 [HttpGet] 拼接后的完整路由。</summary>
internal sealed class EndpointActionInfo
{
    public EndpointActionInfo(string method, string path, AttributeSyntax verbAttribute)
    {
        Method = method;
        NormalizedPath = path;
        VerbAttribute = verbAttribute;
    }

    public string Method { get; }

    public string NormalizedPath { get; }

    /// <summary>HTTP verb 特性（[HttpGet]/[HttpPost]/[HttpDelete]/[HttpPut]），用于定位诊断。</summary>
    public AttributeSyntax VerbAttribute { get; }
}

/// <summary>从控制器 action 语法节点计算完整路由，与 shared-spec.json 的 path 对齐。</summary>
internal static class EndpointRouteHelper
{
    private static readonly Dictionary<string, string> VerbMap = new()
    {
        ["HttpGet"] = "GET",
        ["HttpPost"] = "POST",
        ["HttpDelete"] = "DELETE",
        ["HttpPut"] = "PUT",
    };

    private static readonly Regex RouteParamRegex = new(@"\{[^}]*\}", RegexOptions.Compiled);

    /// <summary>返回方法上所有 HTTP verb 特性对应的 (method, 归一化路径)；非端点 action 返回空列表。</summary>
    public static List<EndpointActionInfo> GetEndpoints(MethodDeclarationSyntax method)
    {
        var result = new List<EndpointActionInfo>();
        ClassDeclarationSyntax? classDecl = method.FirstAncestorOrSelf<ClassDeclarationSyntax>();
        if (classDecl is null)
        {
            return result;
        }

        string? classRoute = GetFirstStringArgument(FindAttribute(classDecl.AttributeLists, "Route"));
        foreach (AttributeSyntax attribute in EnumerateAttributes(method.AttributeLists))
        {
            if (GetSimpleName(attribute) is not string attributeName
                || !VerbMap.TryGetValue(attributeName, out string? verb))
            {
                continue;
            }

            string path = CombineRoute(classRoute, GetFirstStringArgument(attribute));
            if (path.Length == 0)
            {
                continue; // 无路由模板，无法判断
            }
            if (!path.StartsWith("/", System.StringComparison.Ordinal))
            {
                path = "/" + path;
            }
            if (path.Length > 1)
            {
                path = path.TrimEnd('/');
            }
            path = RouteParamRegex.Replace(path, "{x}"); // {shareId} → {x}
            result.Add(new EndpointActionInfo(verb, path, attribute));
        }
        return result;
    }

    /// <summary>
    /// ASP.NET Core 路由拼接：以 / 或 ~/ 开头的方法模板覆盖类路由（~ 前缀剥离），
    /// 否则追加到类路由之后；方法无模板时类路由即完整路由。
    /// </summary>
    private static string CombineRoute(string? classRoute, string? methodTemplate)
    {
        if (methodTemplate is null || methodTemplate.Length == 0)
        {
            return classRoute ?? string.Empty;
        }

        string template = methodTemplate.TrimStart('~');
        if (template.StartsWith("/", System.StringComparison.Ordinal))
        {
            return template;
        }

        string baseRoute = (classRoute ?? string.Empty).TrimEnd('/');
        return baseRoute.Length == 0 ? template : baseRoute + "/" + template;
    }

    /// <summary>在特性列表中查找指定名称的特性（支持 Xxx / XxxAttribute 两种写法）。</summary>
    public static AttributeSyntax? FindAttribute(SyntaxList<AttributeListSyntax> lists, string name)
    {
        foreach (AttributeSyntax attribute in EnumerateAttributes(lists))
        {
            if (GetSimpleName(attribute) is string simpleName
                && (simpleName == name || simpleName == name + "Attribute"))
            {
                return attribute;
            }
        }
        return null;
    }

    public static IEnumerable<AttributeSyntax> EnumerateAttributes(SyntaxList<AttributeListSyntax> lists)
    {
        foreach (AttributeListSyntax list in lists)
        {
            foreach (AttributeSyntax attribute in list.Attributes)
            {
                yield return attribute;
            }
        }
    }

    public static string? GetSimpleName(AttributeSyntax attribute) => attribute.Name switch
    {
        IdentifierNameSyntax identifier => identifier.Identifier.Text,
        QualifiedNameSyntax qualified => qualified.Right.Identifier.Text,
        _ => null
    };

    public static string? GetFirstStringArgument(AttributeSyntax? attribute)
    {
        if (attribute?.ArgumentList is null || attribute.ArgumentList.Arguments.Count == 0)
        {
            return null;
        }

        ExpressionSyntax expression = attribute.ArgumentList.Arguments[0].Expression;
        return expression is LiteralExpressionSyntax literal
            && literal.Kind() == SyntaxKind.StringLiteralExpression
            ? literal.Token.ValueText
            : null;
    }
}

/// <summary>通用过滤与语义辅助。</summary>
internal static class AnalyzerSupport
{
    /// <summary>跳过 Generated/ 目录与 Analyzer 项目自身的文件。</summary>
    public static bool ShouldSkip(string filePath)
    {
        return filePath.Contains("Generated") || filePath.Contains("CloudPan.Analyzers");
    }

    /// <summary>类型自身或其基类/接口是否实现 System.IDisposable（AllInterfaces 含继承接口）。</summary>
    public static bool ImplementsDisposable(ITypeSymbol type)
    {
        if (type is not INamedTypeSymbol named)
        {
            return false;
        }

        foreach (INamedTypeSymbol iface in named.AllInterfaces)
        {
            if (iface.Name == "IDisposable" && iface.ContainingNamespace?.Name == "System")
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>节点是否位于 ControllerBase / Controller 派生类中。</summary>
    public static bool InController(SyntaxNodeAnalysisContext context, SyntaxNode node)
    {
        ClassDeclarationSyntax? classDecl = node.FirstAncestorOrSelf<ClassDeclarationSyntax>();
        if (classDecl is null)
        {
            return false;
        }

        INamedTypeSymbol? symbol = context.SemanticModel.GetDeclaredSymbol(classDecl);
        for (INamedTypeSymbol? baseType = symbol?.BaseType; baseType is not null; baseType = baseType.BaseType)
        {
            if (baseType.Name == "ControllerBase" || baseType.Name == "Controller")
            {
                return true;
            }
        }
        return false;
    }
}
