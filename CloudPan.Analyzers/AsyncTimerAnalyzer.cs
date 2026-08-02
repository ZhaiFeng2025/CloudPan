using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace CloudPan.Analyzers;

/// <summary>
/// CP302: 检测 new System.Threading.Timer(async _ => ...) 形式的异步回调。
/// Timer 回调委托 TimerCallback 返回 void，async lambda 会编译为 async void，
/// 回调内未捕获的异常会直接崩溃进程，应改为同步方法 + Task.Run 或在回调内 try/catch。
/// 通过构造函数符号语义确认目标类型确为 System.Threading.Timer。
/// 排除 Generated/ 目录与 Analyzer 项目自身。
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class AsyncTimerAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "CP302";

    private static readonly string Title = "Timer 回调使用 async lambda";
    private static readonly string MessageFormat = "System.Threading.Timer 回调使用 async lambda（编译为 async void），回调内异常会崩溃进程。建议改用同步回调 + Task.Run，或在回调内 try/catch";
    private static readonly string Description = "Timer 回调委托返回 void，async lambda 是 async void，未捕获异常会崩溃进程.";
    private static readonly string HelpLink = "https://github.com/cloudpan/spec";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId, Title, MessageFormat,
        "Lifecycle", DiagnosticSeverity.Error,
        isEnabledByDefault: true, Description, HelpLink);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeAsyncLambda,
            SyntaxKind.SimpleLambdaExpression, SyntaxKind.ParenthesizedLambdaExpression);
    }

    private void AnalyzeAsyncLambda(SyntaxNodeAnalysisContext context)
    {
        if (AnalyzerSupport.ShouldSkip(context.Node.SyntaxTree.FilePath))
        {
            return;
        }

        // async lambda 由 AsyncKeyword 标识；非 async 的 lambda 不属于 async void 风险
        bool isAsync = context.Node switch
        {
            SimpleLambdaExpressionSyntax simple => simple.AsyncKeyword.IsKind(SyntaxKind.AsyncKeyword),
            ParenthesizedLambdaExpressionSyntax paren => paren.AsyncKeyword.IsKind(SyntaxKind.AsyncKeyword),
            _ => false
        };
        if (!isAsync)
        {
            return;
        }

        var lambda = (LambdaExpressionSyntax)context.Node;

        // 向上找包含该 lambda 的 Timer 构造调用
        ObjectCreationExpressionSyntax? creation = lambda.FirstAncestorOrSelf<ObjectCreationExpressionSyntax>();
        if (creation?.ArgumentList is null)
        {
            return;
        }

        bool isArgument = false;
        foreach (ArgumentSyntax argument in creation.ArgumentList.Arguments)
        {
            if (argument.Expression == lambda)
            {
                isArgument = true;
                break;
            }
        }
        if (!isArgument)
        {
            return;
        }

        // 语义确认构造函数类型为 System.Threading.Timer
        if (context.SemanticModel.GetSymbolInfo(creation).Symbol is not IMethodSymbol constructor
            || constructor.MethodKind != MethodKind.Constructor
            || constructor.ContainingType is not INamedTypeSymbol timerType
            || timerType.Name != "Timer"
            || timerType.ContainingNamespace?.ToDisplayString() != "System.Threading")
        {
            return;
        }

        Diagnostic diagnostic = Diagnostic.Create(Rule, lambda.GetLocation());
        context.ReportDiagnostic(diagnostic);
    }
}
