using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace CloudPan.Analyzers;

/// <summary>
/// CP102: 检测控制器中直接使用 IPAddress.IsLoopback / RemoteIpAddress / HttpContext.Connection。
/// 回环/连接判断应交给 [EndpointAuth(AuthMode.Localhost)] 声明式认证统一处理，避免各控制器重复手写。
/// 仅检查 ControllerBase 派生类中的用法；中间件等场景不受影响。
/// 排除 Generated/ 目录与 Analyzer 项目自身。
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class LoopbackCheckAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "CP102";

    private static readonly string Title = "控制器中直接判断客户端回环/连接";
    private static readonly string MessageFormat = "控制器中直接使用 {0} 判断回环/连接。建议改用 [EndpointAuth(AuthMode.Localhost)] 声明式认证";
    private static readonly string Description = "回环/连接判断应通过 [EndpointAuth(AuthMode.Localhost)] 声明式认证完成，避免在控制器中重复手写.";
    private static readonly string HelpLink = "https://github.com/cloudpan/spec#auth";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId, Title, MessageFormat,
        "Security", DiagnosticSeverity.Warning,
        isEnabledByDefault: true, Description, HelpLink);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeInvocation, SyntaxKind.InvocationExpression);
        context.RegisterSyntaxNodeAction(AnalyzeMemberAccess, SyntaxKind.SimpleMemberAccessExpression);
    }

    private void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
    {
        if (AnalyzerSupport.ShouldSkip(context.Node.SyntaxTree.FilePath)
            || !AnalyzerSupport.InController(context, context.Node))
        {
            return;
        }

        InvocationExpressionSyntax invocation = (InvocationExpressionSyntax)context.Node;
        if (invocation.Expression is not MemberAccessExpressionSyntax access
            || access.Name.Identifier.Text != "IsLoopback")
        {
            return;
        }

        // 仅匹配 IPAddress.IsLoopback（接收者简单名为 IPAddress）
        string receiver = access.Expression.ToString();
        if (receiver != "IPAddress" && !receiver.EndsWith(".IPAddress"))
        {
            return;
        }

        Report(context, access);
    }

    private void AnalyzeMemberAccess(SyntaxNodeAnalysisContext context)
    {
        if (AnalyzerSupport.ShouldSkip(context.Node.SyntaxTree.FilePath)
            || !AnalyzerSupport.InController(context, context.Node))
        {
            return;
        }

        MemberAccessExpressionSyntax access = (MemberAccessExpressionSyntax)context.Node;
        if (access.Name.Identifier.Text == "RemoteIpAddress")
        {
            Report(context, access);
            return;
        }

        // HttpContext.Connection 的直接访问；RemoteIpAddress 场景已由上面的规则报告，避免重复
        if (access.Name.Identifier.Text == "Connection"
            && access.Expression.ToString().EndsWith("HttpContext"))
        {
            if (access.Parent is MemberAccessExpressionSyntax parent
                && parent.Name.Identifier.Text == "RemoteIpAddress")
            {
                return;
            }
            Report(context, access);
        }
    }

    private static void Report(SyntaxNodeAnalysisContext context, MemberAccessExpressionSyntax access)
    {
        Diagnostic diagnostic = Diagnostic.Create(Rule, access.GetLocation(), access.ToString());
        context.ReportDiagnostic(diagnostic);
    }
}
