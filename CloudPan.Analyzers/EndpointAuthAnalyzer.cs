using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace CloudPan.Analyzers;

/// <summary>
/// CP101: 校验端点 [EndpointAuth] 认证声明与 shared-spec.json → api.endpoints 的 auth 字段一致。
/// 特性可在方法或类级别声明（类级别继承），方法与类同时声明时以方法为准。
/// 未声明特性的端点不报告——认证中间件会回退到契约表（见 TokenAuthMiddleware），
/// 仅当显式声明与契约不一致时才报告。
/// 排除 Generated/ 目录与 Analyzer 项目自身。
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class EndpointAuthAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "CP101";

    private static readonly string Title = "端点认证声明与契约不一致";
    private static readonly string MessageFormat = "端点 {0} {1} 的认证声明与 shared-spec.json 不一致：{2}";
    private static readonly string Description = "端点的 [EndpointAuth] 认证要求必须与 shared-spec.json → api.endpoints 的 auth 字段一致.";
    private static readonly string HelpLink = "https://github.com/cloudpan/spec#api.endpoints";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId, Title, MessageFormat,
        "Contract", DiagnosticSeverity.Error,
        isEnabledByDefault: true, Description, HelpLink);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeMethod, SyntaxKind.MethodDeclaration);
    }

    private static void AnalyzeMethod(SyntaxNodeAnalysisContext context)
    {
        if (AnalyzerSupport.ShouldSkip(context.Node.SyntaxTree.FilePath))
        {
            return;
        }

        MethodDeclarationSyntax method = (MethodDeclarationSyntax)context.Node;
        if (!AnalyzerSupport.InController(context, method))
        {
            return;
        }

        ImmutableArray<SpecEndpoint> endpoints = SpecEndpoints.Get(context.Options);
        if (endpoints.IsEmpty)
        {
            return; // 未找到契约文件时静默
        }

        List<EndpointActionInfo> actions = EndpointRouteHelper.GetEndpoints(method);
        if (actions.Count == 0)
        {
            return;
        }

        AttributeSyntax? authAttribute = FindEndpointAuth(method);
        if (authAttribute is null)
        {
            return; // 未声明特性时认证由契约表兜底，不报告
        }

        string? declared = GetDeclaredAuth(authAttribute);
        if (declared is null)
        {
            return; // 参数无法解析（非枚举成员/字符串字面量），不误报
        }

        foreach (EndpointActionInfo action in actions)
        {
            SpecEndpoint? spec = SpecEndpoints.Find(endpoints, action.Method, action.NormalizedPath);
            if (spec is null)
            {
                continue; // 未注册端点由 CP100 报告
            }

            if (!string.Equals(declared, spec.Auth, System.StringComparison.OrdinalIgnoreCase))
            {
                string detail = $"代码声明为 {authAttribute.ArgumentList!.Arguments[0].ToString()}，shared-spec.json 要求 auth={spec.Auth}";
                Diagnostic diagnostic = Diagnostic.Create(
                    Rule, authAttribute.GetLocation(), action.Method, action.NormalizedPath, detail);
                context.ReportDiagnostic(diagnostic);
            }
        }
    }

    /// <summary>方法特性优先，其次类级别特性（继承）。</summary>
    private static AttributeSyntax? FindEndpointAuth(MethodDeclarationSyntax method)
    {
        AttributeSyntax? attribute = EndpointRouteHelper.FindAttribute(method.AttributeLists, "EndpointAuth");
        if (attribute is not null)
        {
            return attribute;
        }

        ClassDeclarationSyntax? classDecl = method.FirstAncestorOrSelf<ClassDeclarationSyntax>();
        return classDecl is null ? null : EndpointRouteHelper.FindAttribute(classDecl.AttributeLists, "EndpointAuth");
    }

    /// <summary>
    /// 提取 [EndpointAuth] 第一个参数对应的认证值：AuthMode.Localhost → "localhost"、
    /// Localhost → "localhost"、字符串字面量 "localhost" → "localhost"。
    /// </summary>
    private static string? GetDeclaredAuth(AttributeSyntax attribute)
    {
        if (attribute.ArgumentList is null || attribute.ArgumentList.Arguments.Count == 0)
        {
            return null;
        }

        ExpressionSyntax expression = attribute.ArgumentList.Arguments[0].Expression;
        string? raw = expression switch
        {
            MemberAccessExpressionSyntax member => member.Name.Identifier.Text,  // AuthMode.Localhost
            IdentifierNameSyntax identifier => identifier.Identifier.Text,        // Localhost
            LiteralExpressionSyntax literal when literal.Kind() == SyntaxKind.StringLiteralExpression => literal.Token.ValueText,
            _ => null
        };
        return raw?.Trim().ToLowerInvariant();
    }
}
