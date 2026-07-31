using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace CloudPan.Analyzers;

/// <summary>
/// CP200: 检测 File.WriteAllText / File.ReadAllText / File.AppendAllText 与 new StreamWriter(...)
/// 对敏感数据的直接写盘。路径参数为字符串字面量且含 token/secret/credential 子串，
/// 或路径来自变量名含这些子串的标识符/成员访问时报告。
/// 敏感数据（令牌/密钥/凭据）应通过 SecretStore 统一保存，避免明文落盘。
/// 排除 Generated/ 目录与 Analyzer 项目自身。
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class SensitiveWriteAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "CP200";

    private static readonly string Title = "敏感数据直接写盘";
    private static readonly string MessageFormat = "敏感数据直接写盘（{0}），请使用 SecretStore 保存令牌/密钥";
    private static readonly string Description = "令牌、密钥、凭据等敏感数据不应通过 File.* / StreamWriter 直接写盘，应使用 SecretStore 统一保存.";
    private static readonly string HelpLink = "https://github.com/cloudpan/spec#config";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId, Title, MessageFormat,
        "Security", DiagnosticSeverity.Warning,
        isEnabledByDefault: true, Description, HelpLink);

    // 敏感关键词：token / secret / credential（大小写不敏感子串匹配）
    private static readonly string[] SensitiveKeywords = { "token", "secret", "credential" };

    private static readonly HashSet<string> FileIoMethods = new(System.StringComparer.Ordinal)
    {
        "WriteAllText", "ReadAllText", "AppendAllText",
    };

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeInvocation, SyntaxKind.InvocationExpression);
        context.RegisterSyntaxNodeAction(AnalyzeObjectCreation, SyntaxKind.ObjectCreationExpression);
    }

    private void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
    {
        if (AnalyzerSupport.ShouldSkip(context.Node.SyntaxTree.FilePath))
        {
            return;
        }

        InvocationExpressionSyntax invocation = (InvocationExpressionSyntax)context.Node;
        if (invocation.Expression is not MemberAccessExpressionSyntax access
            || !FileIoMethods.Contains(access.Name.Identifier.Text))
        {
            return;
        }

        // 仅匹配 System.IO.File 上的静态方法
        string receiver = access.Expression.ToString();
        if (receiver != "File" && !receiver.EndsWith(".File", System.StringComparison.Ordinal))
        {
            return;
        }

        // 路径为第一个实参（WriteAllText/ReadAllText/AppendAllText 的 string path 重载均如此）
        if (invocation.ArgumentList.Arguments.Count == 0
            || !IsSensitivePath(invocation.ArgumentList.Arguments[0].Expression))
        {
            return;
        }

        Diagnostic diagnostic = Diagnostic.Create(Rule, access.GetLocation(), invocation.ToString());
        context.ReportDiagnostic(diagnostic);
    }

    private void AnalyzeObjectCreation(SyntaxNodeAnalysisContext context)
    {
        if (AnalyzerSupport.ShouldSkip(context.Node.SyntaxTree.FilePath))
        {
            return;
        }

        ObjectCreationExpressionSyntax creation = (ObjectCreationExpressionSyntax)context.Node;
        if (!IsStreamWriter(creation.Type)
            || creation.ArgumentList is null
            || creation.ArgumentList.Arguments.Count == 0)
        {
            return;
        }

        // StreamWriter(string path) 系列重载的路径为第一个实参；
        // StreamWriter(Stream) 等重载传入的流变量名不会命中敏感关键词，天然不误报
        if (!IsSensitivePath(creation.ArgumentList.Arguments[0].Expression))
        {
            return;
        }

        Diagnostic diagnostic = Diagnostic.Create(Rule, creation.GetLocation(), creation.ToString());
        context.ReportDiagnostic(diagnostic);
    }

    /// <summary>类型是否为 StreamWriter（含 System.IO.StreamWriter 限定名）。</summary>
    private static bool IsStreamWriter(TypeSyntax type) => type switch
    {
        IdentifierNameSyntax identifier => identifier.Identifier.Text == "StreamWriter",
        QualifiedNameSyntax qualified => qualified.Right.Identifier.Text == "StreamWriter",
        _ => false
    };

    /// <summary>路径表达式是否指向敏感数据：字符串字面量或变量名含 token/secret/credential 子串。</summary>
    private static bool IsSensitivePath(ExpressionSyntax expression)
    {
        switch (expression)
        {
            case LiteralExpressionSyntax literal when literal.Kind() == SyntaxKind.StringLiteralExpression:
                return ContainsSensitiveKeyword(literal.Token.ValueText);
            case IdentifierNameSyntax identifier:
                return ContainsSensitiveKeyword(identifier.Identifier.Text);
            case MemberAccessExpressionSyntax member:
                return ContainsSensitiveKeyword(member.Name.Identifier.Text);
            default:
                return false;
        }
    }

    private static bool ContainsSensitiveKeyword(string text)
    {
        foreach (string keyword in SensitiveKeywords)
        {
            if (text.IndexOf(keyword, System.StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
        }
        return false;
    }
}
