using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace CloudPan.Analyzers;

/// <summary>
/// CP300: 检测 CloudPan.*.Services 命名空间内的 event += 订阅。
/// 事件订阅必须可退订：所在类型应实现 IDisposable，并在 Dispose() 中取消订阅，避免内存泄漏。
/// 注：CP304（未实现 IDisposable 的 Error 场景）已于 v1.0.0 清理时移除——该规则从未在任何配置中启用。
/// Dispose() 在基类实现时无法在本类验证，跳过（不误报）。
/// 排除 Generated/ 目录与 Analyzer 项目自身。
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class EventSubscriptionAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "CP300";

    private static readonly string TitleNoUnsubscribe = "Services 类型事件订阅但 Dispose 未取消";
    private static readonly string MessageFormatNoUnsubscribe = "类 {0} 在 Dispose() 中未取消事件 {1} 的订阅";
    private static readonly string Description = "事件订阅必须可退订：所在类型应实现 IDisposable，并在 Dispose() 中取消订阅，避免内存泄漏.";
    private static readonly string HelpLink = "https://github.com/cloudpan/spec";

    private static readonly DiagnosticDescriptor RuleNoUnsubscribe = new(
        DiagnosticId, TitleNoUnsubscribe, MessageFormatNoUnsubscribe,
        "Lifecycle", DiagnosticSeverity.Warning,
        isEnabledByDefault: true, Description, HelpLink);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics
        => ImmutableArray.Create(RuleNoUnsubscribe);

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

        // 仅 CloudPan.*.Services 命名空间
        INamespaceSymbol? ns = context.SemanticModel.GetEnclosingSymbol(context.Node.SpanStart)?.ContainingNamespace;
        string nsName = ns?.ToDisplayString() ?? string.Empty;
        if (!nsName.StartsWith("CloudPan.", System.StringComparison.Ordinal)
            || !nsName.EndsWith(".Services", System.StringComparison.Ordinal))
        {
            return;
        }

        // 订阅语句所在的类型声明
        TypeDeclarationSyntax? typeDecl = context.Node.FirstAncestorOrSelf<TypeDeclarationSyntax>();
        if (typeDecl is null)
        {
            return;
        }
        INamedTypeSymbol? typeSymbol = context.SemanticModel.GetDeclaredSymbol(typeDecl);
        if (typeSymbol is null)
        {
            return;
        }

        // 本类声明的无参 Dispose() 方法体；Dispose 在基类时无法在本类验证，跳过
        MethodDeclarationSyntax? disposeMethod = null;
        foreach (MemberDeclarationSyntax member in typeDecl.Members)
        {
            if (member is MethodDeclarationSyntax method
                && method.Identifier.ValueText == "Dispose"
                && method.ParameterList.Parameters.Count == 0)
            {
                disposeMethod = method;
                break;
            }
        }
        if (disposeMethod?.Body is null)
        {
            return;
        }

        // Dispose() 中存在同一事件的 -= 即视为已退订
        foreach (SyntaxNode node in disposeMethod.Body.DescendantNodesAndSelf())
        {
            if (node is AssignmentExpressionSyntax unsubscribe
                && unsubscribe.IsKind(SyntaxKind.SubtractAssignmentExpression)
                && context.SemanticModel.GetSymbolInfo(unsubscribe.Left).Symbol is IEventSymbol unsubscribed
                && SymbolEqualityComparer.Default.Equals(unsubscribed, eventSymbol))
            {
                return;
            }
        }

        Diagnostic warning = Diagnostic.Create(
            RuleNoUnsubscribe, assignment.GetLocation(), typeSymbol.Name, eventSymbol.Name);
        context.ReportDiagnostic(warning);
    }
}
