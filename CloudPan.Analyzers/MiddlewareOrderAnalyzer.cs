using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace CloudPan.Analyzers;

/// <summary>
/// CP400: 检测 ASP.NET Core 中间件管道中 UseRateLimit 在 UseTokenAuth 之前注册。
/// RateLimitMiddleware 依赖 TokenAuthMiddleware 写入的 context.Items["DeviceId"] 进行按设备限流，
/// 若 RateLimit 先于 TokenAuth 执行，则 DeviceId 永远为 null，限流退化为 IP 级别，
/// NAT 后多设备共享同一配额。
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class MiddlewareOrderAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "CP400";

    private static readonly string Title = "中间件顺序错误：UseRateLimit 必须在 UseTokenAuth 之后";
    private static readonly string MessageFormat = "UseRateLimit() 在 UseTokenAuth() 之前调用，导致限流永远无法按设备识别（context.Items[\"DeviceId\"] 尚未设置）。请交换顺序：先 UseTokenAuth() 后 UseRateLimit()";
    private static readonly string Description =
        "RateLimitMiddleware 从 context.Items[\"DeviceId\"] 读取设备 ID 进行按设备限流，" +
        "该值由 TokenAuthMiddleware 设置。若 UseRateLimit 先于 UseTokenAuth 注册，" +
        "DeviceId 永远为 null，限流退化为 IP 级别。";
    private static readonly string HelpLink = "https://github.com/cloudpan/spec";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId, Title, MessageFormat,
        "Correctness", DiagnosticSeverity.Error,
        isEnabledByDefault: true, Description, HelpLink);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeInvocation, SyntaxKind.InvocationExpression);
    }

    private void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
    {
        if (AnalyzerSupport.ShouldSkip(context.Node.SyntaxTree.FilePath))
            return;

        var invocation = (InvocationExpressionSyntax)context.Node;

        // 仅匹配 app.UseRateLimit() 形式的调用
        if (!IsBuilderMethod(invocation, "UseRateLimit"))
            return;

        // 向上找到包含该调用的方法体
        BlockSyntax? methodBody = invocation.FirstAncestorOrSelf<BlockSyntax>();
        if (methodBody is null)
            return;

        // 在同一方法体内，检查 UseRateLimit 之前是否存在 UseTokenAuth 调用
        // （遍历方法体内的调用，比较位置）
        bool foundTokenAuthBefore = false;
        foreach (InvocationExpressionSyntax inv in methodBody.DescendantNodes()
                     .OfType<InvocationExpressionSyntax>())
        {
            if (inv.SpanStart >= invocation.SpanStart)
                continue; // 只检查 UseRateLimit 之前的调用

            if (IsBuilderMethod(inv, "UseTokenAuth"))
            {
                foundTokenAuthBefore = true;
                break;
            }
        }

        // 如果 UseTokenAuth 不在 UseRateLimit 之前，报错
        if (!foundTokenAuthBefore)
        {
            // 但仍需确认 UseTokenAuth 在同一个方法体中存在（在 UseRateLimit 之后）
            bool hasTokenAuthAfter = methodBody.DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .Any(inv => inv.SpanStart > invocation.SpanStart && IsBuilderMethod(inv, "UseTokenAuth"));

            if (hasTokenAuthAfter)
            {
                Diagnostic diagnostic = Diagnostic.Create(Rule, invocation.GetLocation());
                context.ReportDiagnostic(diagnostic);
            }
        }
    }

    /// <summary>判断调用是否为 app.Xxx() 形式且方法名匹配。</summary>
    private static bool IsBuilderMethod(InvocationExpressionSyntax invocation, string methodName)
    {
        return invocation.Expression is MemberAccessExpressionSyntax memberAccess
               && memberAccess.Name.Identifier.Text == methodName
               && invocation.ArgumentList.Arguments.Count == 0;
    }
}
