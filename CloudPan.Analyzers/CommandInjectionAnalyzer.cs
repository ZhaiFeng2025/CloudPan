using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace CloudPan.Analyzers;

/// <summary>
/// CP404: 检测 Process.Start("cmd.exe", ...) 命令注入风险。
/// 命令字符串中包含插值表达式（$"..."）且插值不是编译时常量时，
/// cmd.exe 元字符（&amp; | &lt; &gt; ^）可被利用执行任意命令。
/// 应改用 RunExe(executable, args...) 直接调用目标可执行文件，绕过 cmd.exe。
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class CommandInjectionAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "CP404";

    private static readonly string Title = "潜在命令注入——cmd.exe 参数含非常量输入";
    private static readonly string MessageFormat = "Process.Start(\"cmd.exe\", ...) 参数包含插值字符串且插值不是编译时常量，存在命令注入风险。请改用 RunExe(executable, args...) 直接调用可执行文件，绕过 cmd.exe 元字符解析";
    private static readonly string Description =
        "将用户输入拼接到 cmd.exe /c 命令中存在命令注入风险。改用直接可执行文件调用.";
    private static readonly string HelpLink = "https://github.com/cloudpan/spec";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId, Title, MessageFormat,
        "Security", DiagnosticSeverity.Error,
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

        // 匹配 Process.Start(string, string) 或 Process.Start("cmd.exe", ...)
        if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess
            || memberAccess.Name.Identifier.Text != "Start")
            return;

        if (context.SemanticModel.GetSymbolInfo(invocation).Symbol is not IMethodSymbol method
            || method.ContainingType?.Name != "Process"
            || method.ContainingType?.ContainingNamespace?.ToDisplayString() != "System.Diagnostics")
            return;

        var args = invocation.ArgumentList.Arguments;
        if (args.Count < 2)
            return;

        // 第一个参数必须是 "cmd.exe" 字面量
        if (args[0].Expression is not LiteralExpressionSyntax firstArgLiteral
            || firstArgLiteral.Token.ValueText?.ToLowerInvariant() != "cmd.exe")
            return;

        // 第二个参数包含非编译时常量插值
        ExpressionSyntax secondArg = args[1].Expression;
        if (ContainsNonConstantInterpolation(secondArg, context.SemanticModel))
        {
            Diagnostic diagnostic = Diagnostic.Create(Rule, invocation.GetLocation());
            context.ReportDiagnostic(diagnostic);
        }
    }

    private static bool ContainsNonConstantInterpolation(ExpressionSyntax expr, SemanticModel model)
    {
        if (expr is InterpolatedStringExpressionSyntax interpolated)
        {
            foreach (var content in interpolated.Contents)
            {
                if (content is InterpolationSyntax ins
                    && ins.Expression is not LiteralExpressionSyntax)
                {
                    var cv = model.GetConstantValue(ins.Expression);
                    if (!cv.HasValue) return true;
                }
            }
        }

        if (expr is BinaryExpressionSyntax binary)
        {
            return ContainsNonConstantInterpolation(binary.Left, model)
                   || ContainsNonConstantInterpolation(binary.Right, model);
        }

        return false;
    }
}
