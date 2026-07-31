using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace CloudPan.Analyzers;

/// <summary>
/// CP301: 检测 event += 的右侧为匿名 lambda / 匿名方法。
/// 匿名 lambda 无法退订（-= 需要可引用的委托实例），建议改为具名方法。
/// 通过语义确认左侧确为事件符号，避免误报普通委托赋值。
/// 排除 Generated/ 目录与 Analyzer 项目自身。
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class LambdaSubscriptionAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "CP301";

    private static readonly string Title = "事件订阅使用匿名 lambda 无法退订";
    private static readonly string MessageFormat = "事件 {0} 使用匿名 lambda 订阅，无法退订。建议改为具名方法以便在 Dispose() 中取消订阅";
    private static readonly string Description = "匿名 lambda 订阅的事件无法退订（-= 需要可引用的委托实例），应改为具名方法.";
    private static readonly string HelpLink = "https://github.com/cloudpan/spec";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId, Title, MessageFormat,
        "Lifecycle", DiagnosticSeverity.Warning,
        isEnabledByDefault: true, Description, HelpLink);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeSubscription, SyntaxKind.AddAssignmentExpression);
    }

    private void AnalyzeSubscription(SyntaxNodeAnalysisContext context)
    {
        if (AnalyzerSupport.ShouldSkip(context.Node.SyntaxTree.FilePath))
        {
            return;
        }

        var assignment = (AssignmentExpressionSyntax)context.Node;

        // 仅事件订阅（+= 左侧解析为事件符号）
        if (context.SemanticModel.GetSymbolInfo(assignment.Left).Symbol is not IEventSymbol eventSymbol)
        {
            return;
        }

        // 右侧为 lambda 或匿名方法（async lambda 也是 LambdaExpressionSyntax 子类，一并覆盖）
        if (assignment.Right is not (LambdaExpressionSyntax or AnonymousMethodExpressionSyntax))
        {
            return;
        }

        Diagnostic diagnostic = Diagnostic.Create(Rule, assignment.Right.GetLocation(), eventSymbol.Name);
        context.ReportDiagnostic(diagnostic);
    }
}
