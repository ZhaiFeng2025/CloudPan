using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace CloudPan.Analyzers;

/// <summary>
/// CP401: 检测 System.Threading.Timer 回调或 void 返回类型方法内部使用 `_ = SomeAsync()`
/// fire-and-forget 模式。异步方法在丢弃 Task 后发生的异常会被 TaskScheduler 静默吞掉，
/// 导致 DB 写入失败、连接关闭失败等关键操作静默丢失。
/// 配合 CP302（检测 Timer 中 async lambda）覆盖 Timer 异步回调的全部风险模式。
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class FireAndForgetAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "CP401";

    private static readonly string Title = "Timer 或 void 方法中的 fire-and-forget 异步调用";
    private static readonly string MessageFormat = "在 {0} 中使用 `_ = {1}()` 丢弃 Task。若该方法为异步操作，异常会被 TaskScheduler 静默吞掉，导致关键操作（DB 写入、连接关闭）静默失败。请改为 await 调用（需将所在方法改为 async），或在 Task.Run 中包裹 try-catch";
    private static readonly string Description =
        "在 Timer 回调或 void 返回方法中使用 _ = AsyncMethod() 丢弃 Task，Task 内部异常可能静默丢失。应改为 await 或包裹 try-catch.";
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
        context.RegisterSyntaxNodeAction(AnalyzeDiscardAssignment, SyntaxKind.SimpleAssignmentExpression);
    }

    private void AnalyzeDiscardAssignment(SyntaxNodeAnalysisContext context)
    {
        if (AnalyzerSupport.ShouldSkip(context.Node.SyntaxTree.FilePath))
            return;

        var assignment = (AssignmentExpressionSyntax)context.Node;

        // 匹配 _ = Xxx() 模式（左侧为 discard）
        if (assignment.Left is not IdentifierNameSyntax leftId
            || leftId.Identifier.Text != "_")
            return;

        // 右侧必须是一个调用
        if (assignment.Right is not InvocationExpressionSyntax invocation)
            return;

        // 检查被调用方法的返回类型是否为 Task/Task<T>（确认是丢弃异步调用）
        if (context.SemanticModel.GetSymbolInfo(invocation).Symbol is not IMethodSymbol methodSymbol)
            return;

        if (methodSymbol.ReturnType is not INamedTypeSymbol returnType)
            return;

        // 仅当返回 Task / Task<T> / ValueTask / ValueTask<T> 时才标记
        if (!IsTaskLike(returnType))
            return;

        // 向上查找包含 discard 的方法/匿名函数
        MethodDeclarationSyntax? containingMethod = assignment.FirstAncestorOrSelf<MethodDeclarationSyntax>();
        AnonymousFunctionExpressionSyntax? containingLambda = assignment.FirstAncestorOrSelf<AnonymousFunctionExpressionSyntax>();

        string containerType = "此上下文";

        // 场景 1：在 void 返回类型的方法内部
        if (containingMethod?.ReturnType is PredefinedTypeSyntax predefined
            && predefined.Keyword.IsKind(SyntaxKind.VoidKeyword))
        {
            containerType = $"void 方法 '{containingMethod.Identifier.Text}'";
        }
        // 场景 2：在 async void lambda 内部
        else if (containingLambda is LambdaExpressionSyntax lambda
                 && lambda.AsyncKeyword.IsKind(SyntaxKind.AsyncKeyword))
        {
            // async void lambda 可通过检查返回类型判断——此处简化处理
            containerType = "async void lambda";
        }
        // 场景 3：在 System.Threading.Timer 构造的回调参数内
        else if (IsInsideTimerConstructor(assignment))
        {
            containerType = "System.Threading.Timer 回调";
        }
        else
        {
            return; // 不在此规则范围内（普通 async Task 方法中的 _ = 是可以接受的）
        }

        string methodName = GetMethodName(invocation);
        Diagnostic diagnostic = Diagnostic.Create(Rule, assignment.GetLocation(), containerType, methodName);
        context.ReportDiagnostic(diagnostic);
    }

    private static bool IsTaskLike(INamedTypeSymbol type)
    {
        return type.Name is "Task" or "ValueTask"
               || (type.IsGenericType && type.Name is "Task" or "ValueTask")
               && type.ContainingNamespace?.ToDisplayString() == "System.Threading.Tasks";
    }

    private static bool IsInsideTimerConstructor(SyntaxNode node)
    {
        ObjectCreationExpressionSyntax? creation = node.FirstAncestorOrSelf<ObjectCreationExpressionSyntax>();
        if (creation is null) return false;

        // 向上找到包含该 lambda 的参数
        LambdaExpressionSyntax? lambda = node.FirstAncestorOrSelf<LambdaExpressionSyntax>();
        if (lambda is null) return false;

        // 确认 lambda 是 Timer 构造函数的参数
        bool isArgument = creation.ArgumentList?.Arguments
            .Any(a => a.Expression == lambda || a.Expression.DescendantNodesAndSelf().Contains(lambda)) == true;

        return isArgument;
    }

    private static string GetMethodName(InvocationExpressionSyntax invocation)
    {
        return invocation.Expression switch
        {
            MemberAccessExpressionSyntax ma => ma.Name.Identifier.Text,
            IdentifierNameSyntax id => id.Identifier.Text,
            _ => "UnknownMethod"
        };
    }
}
