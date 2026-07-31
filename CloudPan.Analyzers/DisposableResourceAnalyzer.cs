using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace CloudPan.Analyzers;

/// <summary>
/// CP303: 检测类型实现 IDisposable 的局部变量声明（局部声明语句）：
/// 既未使用 using 语句（含 using var）、也未在后续代码中被持有（任何除声明外的引用），
/// 视为「可释放资源局部变量未持有」，存在资源泄漏风险。
/// 检测范围限定在声明所在执行体（方法体 / lambda 体 / 局部函数体）之内，按符号语义匹配引用。
/// 排除 Generated/ 目录与 Analyzer 项目自身。
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class DisposableResourceAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "CP303";

    private static readonly string Title = "可释放资源局部变量未持有";
    private static readonly string MessageFormat = "局部变量 {0} 的类型实现 IDisposable 但未持有：未用 using 语句、未赋值给字段、也未传递给其他方法，可能造成资源泄漏";
    private static readonly string Description = "可释放资源局部变量应通过 using 语句、赋值给字段或传递给其他方法持有，否则资源可能泄漏.";
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
        context.RegisterSyntaxNodeAction(AnalyzeLocalDeclaration, SyntaxKind.LocalDeclarationStatement);
    }

    private void AnalyzeLocalDeclaration(SyntaxNodeAnalysisContext context)
    {
        if (AnalyzerSupport.ShouldSkip(context.Node.SyntaxTree.FilePath))
        {
            return;
        }

        var statement = (LocalDeclarationStatementSyntax)context.Node;

        // using 语句（含 using var / await using）内的声明由编译器保证释放
        if (statement.UsingKeyword != default)
        {
            return;
        }

        foreach (VariableDeclaratorSyntax declarator in statement.Declaration.Variables)
        {
            if (context.SemanticModel.GetDeclaredSymbol(declarator) is not ILocalSymbol symbol)
            {
                continue;
            }
            if (!AnalyzerSupport.ImplementsDisposable(symbol.Type))
            {
                continue;
            }
            if (HasUseOutsideDeclaration(context, symbol, declarator))
            {
                continue;
            }

            Diagnostic diagnostic = Diagnostic.Create(Rule, declarator.GetLocation(), symbol.Name);
            context.ReportDiagnostic(diagnostic);
        }
    }

    /// <summary>
    /// 声明所在执行体的最外层块内是否有该变量的引用（按符号语义匹配）。
    /// 无法确定执行体（如属性初始化器）时保守返回 true，避免误报。
    /// </summary>
    private static bool HasUseOutsideDeclaration(
        SyntaxNodeAnalysisContext context, ILocalSymbol symbol, VariableDeclaratorSyntax declarator)
    {
        BlockSyntax? outermostBlock = FindOutermostBlock(declarator);
        if (outermostBlock is null)
        {
            return true;
        }

        foreach (IdentifierNameSyntax identifier in outermostBlock.DescendantNodes().OfType<IdentifierNameSyntax>())
        {
            if (identifier.Identifier.ValueText != symbol.Name)
            {
                continue;
            }
            ISymbol? used = context.SemanticModel.GetSymbolInfo(identifier).Symbol;
            if (used is not null && SymbolEqualityComparer.Default.Equals(used, symbol))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>向上取声明所在的执行体（方法体 / lambda 体 / 局部函数体 / 全局语句）最外层块。</summary>
    private static BlockSyntax? FindOutermostBlock(SyntaxNode node)
    {
        BlockSyntax? outermost = null;
        for (SyntaxNode? current = node.Parent; current is not null; current = current.Parent)
        {
            if (current is BlockSyntax block)
            {
                outermost = block;
            }
            if (current is MemberDeclarationSyntax
                or GlobalStatementSyntax
                or AnonymousFunctionExpressionSyntax
                or LocalFunctionStatementSyntax)
            {
                break;
            }
        }
        return outermost;
    }
}
