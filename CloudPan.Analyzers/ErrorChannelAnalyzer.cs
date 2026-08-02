using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace CloudPan.Analyzers;

/// <summary>
/// CP002: 检测匿名对象或手写 JSON 作为错误响应体。
/// 强制所有错误响应通过 ApiErrors 工厂方法创建，确保错误体格式与 spec api.errorResponse 一致。
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ErrorChannelAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "CP002";

    private static readonly string Title = "禁止手写错误响应体";
    private static readonly string MessageFormat = "检测到手写错误响应体。请改用 ApiErrors 工厂方法（如 ApiErrors.BadRequest(message, friendlyMessage)）";
    private static readonly string Description = "所有错误响应必须通过 ApiErrors 工厂创建，确保错误体格式与 shared-spec.json → api.errorResponse 一致.";

    // 目标方法：BadRequest(), NotFound(), Conflict(), StatusCode(), ObjectResult()
    private static readonly HashSet<string> ErrorReturnMethods = new()
    {
        "BadRequest", "NotFound", "Conflict", "StatusCode"
    };

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId, Title, MessageFormat,
        "Contract", DiagnosticSeverity.Error,
        isEnabledByDefault: true, Description,
        helpLinkUri: "https://github.com/cloudpan/spec#api.errorResponse");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeInvocation, SyntaxKind.InvocationExpression);
        // 也检查中间件中的原始 JSON WriteAsync 调用
        context.RegisterSyntaxNodeAction(AnalyzeWriteAsync, SyntaxKind.InvocationExpression);
    }

    private void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
    {
        string filePath = context.Node.SyntaxTree.FilePath;
        if (filePath.Contains("Generated") || filePath.Contains("CloudPan.Analyzers"))
        {
            return;
        }

        InvocationExpressionSyntax invocation = (InvocationExpressionSyntax)context.Node;

        // 检查是否是 BadRequest/NotFound/Conflict/StatusCode 调用
        string? methodName = GetMethodName(invocation);
        if (methodName == null || !ErrorReturnMethods.Contains(methodName))
        {
            return;
        }

        // 检查参数中是否包含匿名对象
        var args = invocation.ArgumentList?.Arguments;
        if (args == null || args.Value.Count == 0)
        {
            return;
        }

        foreach (var arg in args.Value)
        {
            if (arg.Expression is AnonymousObjectCreationExpressionSyntax anonObj)
            {
                // 检查匿名对象是否包含 "code" 字段（错误响应特征）
                foreach (var init in anonObj.Initializers)
                {
                    if (init is AnonymousObjectMemberDeclaratorSyntax member
                        && member.NameEquals?.Name.Identifier.Text == "code")
                    {
                        Diagnostic diagnostic = Diagnostic.Create(Rule, invocation.GetLocation());
                        context.ReportDiagnostic(diagnostic);
                        return;
                    }
                }
            }
            // 也检查 ObjectCreationExpressionSyntax（new ErrorResponse(...) 是 ok 的，但 new { ... } 不是）
        }
    }

    private void AnalyzeWriteAsync(SyntaxNodeAnalysisContext context)
    {
        string filePath = context.Node.SyntaxTree.FilePath;
        if (filePath.Contains("Generated") || filePath.Contains("CloudPan.Analyzers"))
        {
            return;
        }

        InvocationExpressionSyntax invocation = (InvocationExpressionSyntax)context.Node;

        string? methodName = GetMethodName(invocation);
        if (methodName != "WriteAsync")
        {
            return;
        }

        // 检查是否是 context.Response.WriteAsync 调用
        if (invocation.Expression is MemberAccessExpressionSyntax memberAccess)
        {
            string expr = memberAccess.Expression.ToString();
            if (expr.Contains("Response") || expr.Contains("WriteAsync"))
            {
                var args = invocation.ArgumentList?.Arguments;
                if (args != null && args.Value.Count > 0)
                {
                    var firstArg = args.Value[0].Expression;
                    // 检测手写 JSON 字符串（以 { 开头的原始字符串）
                    if (firstArg is LiteralExpressionSyntax literal
                        && literal.Kind() == SyntaxKind.StringLiteralExpression
                        && literal.Token.ValueText.TrimStart().StartsWith("{"))
                    {
                        // 排除 ApiErrors/ErrorResponse 产生的调用（通过检查调用者）
                        Diagnostic diag = Diagnostic.Create(Rule, invocation.GetLocation());
                        context.ReportDiagnostic(diag);
                    }
                }
            }
        }
    }

    private static string? GetMethodName(InvocationExpressionSyntax invocation)
    {
        return invocation.Expression switch
        {
            MemberAccessExpressionSyntax ma => ma.Name.Identifier.Text,
            IdentifierNameSyntax id => id.Identifier.Text,
            _ => null
        };
    }
}
