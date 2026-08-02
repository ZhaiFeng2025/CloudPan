using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace CloudPan.Analyzers;

/// <summary>
/// CP402: 检测 catch(DbUpdateException) 块中重用 try 块创建的 DbContext 实例。
/// SaveChangesAsync 失败后，DbContext 变更追踪器中仍跟踪 Add 失败的实体（状态=Added），
/// 此时 FindAsync 会优先返回变更追踪器中的失败实体而非数据库真值，
/// 导致后续 SaveChangesAsync 再次抛出 DbUpdateException（二次崩溃）。
/// 应在 catch 块中使用全新的 DbContext 实例重试。
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class DbContextRetryAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "CP402";

    private static readonly string Title = "catch(DbUpdateException) 块中重用同一 DbContext 存在二次崩溃风险";
    private static readonly string MessageFormat = "在 catch(DbUpdateException) 块中使用了与 try 块相同的 DbContext 变量 '{0}'。SaveChangesAsync 失败后变更追踪器仍跟踪 Add 失败的实体，FindAsync 会返回该失败实体而非数据库真值，导致二次崩溃。请使用全新的 DbContext 实例（await using var freshDb = await dbFactory.CreateDbContextAsync()）重试";
    private static readonly string Description =
        "catch(DbUpdateException) 中重用同一 DbContext 会导致 FindAsync 从变更追踪器返回 Add 失败的实体，" +
        "二次 SaveChangesAsync 会再次抛 DbUpdateException。应创建全新的 DbContext。";
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
        context.RegisterSyntaxNodeAction(AnalyzeTryStatement, SyntaxKind.TryStatement);
    }

    private void AnalyzeTryStatement(SyntaxNodeAnalysisContext context)
    {
        if (AnalyzerSupport.ShouldSkip(context.Node.SyntaxTree.FilePath))
            return;

        var tryStatement = (TryStatementSyntax)context.Node;

        foreach (CatchClauseSyntax catchClause in tryStatement.Catches)
        {
            // 检查 catch 是否捕获 DbUpdateException
            if (!IsDbUpdateExceptionCatch(catchClause, context.SemanticModel))
                continue;

            // 查找 try 块中 await using var (db) = ...CreateDbContextAsync() 模式
            var tryBlockDbContexts = FindDbContextVariables(tryStatement.Block, context.SemanticModel);
            if (tryBlockDbContexts.Count == 0)
                continue;

            // 查找 catch 块中是否使用了相同的 DbContext 变量
            foreach (string dbVarName in tryBlockDbContexts)
            {
                bool usedInCatch = UsesVariable(catchClause.Block, dbVarName, context.SemanticModel);
                if (usedInCatch)
                {
                    // 检查是否使用了新的 factory.CreateDbContextAsync 创建新实例
                    if (!HasFreshDbContextCreation(catchClause.Block, context.SemanticModel))
                    {
                        var location = catchClause.CatchKeyword.GetLocation();
                        Diagnostic diagnostic = Diagnostic.Create(Rule, location, dbVarName);
                        context.ReportDiagnostic(diagnostic);
                    }
                }
            }
        }
    }

    /// <summary>检查 catch 子句是否捕获 DbUpdateException。</summary>
    private static bool IsDbUpdateExceptionCatch(CatchClauseSyntax catchClause, SemanticModel model)
    {
        if (catchClause.Declaration is null)
            return false;

        var typeSymbol = model.GetTypeInfo(catchClause.Declaration.Type).Type;
        return typeSymbol?.Name == "DbUpdateException"
               && typeSymbol.ContainingNamespace?.ToDisplayString() == "Microsoft.EntityFrameworkCore";
    }

    /// <summary>查找 try 块中通过 IDbContextFactory.CreateDbContextAsync 创建的变量。</summary>
    private static System.Collections.Generic.List<string> FindDbContextVariables(
        BlockSyntax? block, SemanticModel model)
    {
        var result = new System.Collections.Generic.List<string>();
        if (block is null) return result;

        foreach (var statement in block.DescendantNodes().OfType<LocalDeclarationStatementSyntax>())
        {
            if (!statement.UsingKeyword.IsKind(SyntaxKind.UsingKeyword)
                && !statement.AwaitKeyword.IsKind(SyntaxKind.AwaitKeyword))
                continue;

            foreach (var variable in statement.Declaration.Variables)
            {
                if (variable.Initializer?.Value is not InvocationExpressionSyntax invocation)
                    continue;

                // 匹配 factory.CreateDbContextAsync() 模式
                if (invocation.Expression is MemberAccessExpressionSyntax ma
                    && ma.Name.Identifier.Text is "CreateDbContextAsync" or "CreateDbContext")
                {
                    // 检查返回类型是否包含 DbContext
                    var typeInfo = model.GetTypeInfo(invocation).Type;
                    if (typeInfo is INamedTypeSymbol namedType && IsDbContext(namedType))
                    {
                        result.Add(variable.Identifier.Text);
                    }
                }
            }
        }
        return result;
    }

    /// <summary>检查类型是否为 DbContext 派生类。</summary>
    private static bool IsDbContext(INamedTypeSymbol type)
    {
        for (var baseType = type.BaseType; baseType is not null; baseType = baseType.BaseType)
        {
            if (baseType.Name == "DbContext"
                && baseType.ContainingNamespace?.ToDisplayString() == "Microsoft.EntityFrameworkCore")
                return true;
        }
        return false;
    }

    /// <summary>检查代码块中是否使用了指定变量。</summary>
    private static bool UsesVariable(BlockSyntax? block, string varName, SemanticModel model)
    {
        if (block is null) return false;
        return block.DescendantNodes().OfType<IdentifierNameSyntax>()
            .Any(id => id.Identifier.Text == varName
                       && model.GetSymbolInfo(id).Symbol is ILocalSymbol);
    }

    /// <summary>检查 catch 块中是否创建了全新的 DbContext 实例。</summary>
    private static bool HasFreshDbContextCreation(BlockSyntax? block, SemanticModel model)
    {
        if (block is null) return false;
        return block.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Any(inv => inv.Expression is MemberAccessExpressionSyntax ma
                        && (ma.Name.Identifier.Text == "CreateDbContextAsync"
                            || ma.Name.Identifier.Text == "CreateDbContext"));
    }
}
