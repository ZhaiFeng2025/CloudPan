using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace CloudPan.Analyzers;

/// <summary>
/// CP001: 检测错误码形态的字符串字面量（如 "BAD_REQUEST"、"NOT_FOUND" 等）。
/// 强制使用生成的 HttpErrorCode.* 常量替代手写字符串。
/// 排除 Generated/ 目录和 Analyzer 项目自身。
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ErrorCodeLiteralAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "CP001";

    private static readonly string Title = "禁止手写错误码字符串字面量";
    private static readonly string MessageFormat = "检测到错误码字符串字面量 \"{0}\"。请改用 HttpErrorCode.{1} 常量引用";
    private static readonly string Description = "所有错误码必须通过生成的 HttpErrorCode 类引用，而非手写字符串，确保错误码与 shared-spec.json 契约一致.";

    // 匹配大写+数字+下划线组合的错误码形态（如 BAD_REQUEST、INVALID_DEVICE_ID）
    private static readonly Regex ErrorCodePattern = new(@"^[A-Z][A-Z0-9_]{3,}$", RegexOptions.Compiled);

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId, Title, MessageFormat,
        "Contract", DiagnosticSeverity.Error,
        isEnabledByDefault: true, Description,
        helpLinkUri: "https://github.com/cloudpan/spec#HttpErrorCode");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeStringLiteral, Microsoft.CodeAnalysis.CSharp.SyntaxKind.StringLiteralExpression);
    }

    private void AnalyzeStringLiteral(SyntaxNodeAnalysisContext context)
    {
        // 跳过 Generated/ 目录
        string filePath = context.Node.SyntaxTree.FilePath;
        if (filePath.Contains("Generated") || filePath.Contains("CloudPan.Analyzers"))
        {
            return;
        }

        LiteralExpressionSyntax literal = (Microsoft.CodeAnalysis.CSharp.Syntax.LiteralExpressionSyntax)context.Node;
        string text = literal.Token.ValueText;

        // 只匹配错误码形态的字符串
        if (!ErrorCodePattern.IsMatch(text))
        {
            return;
        }

        // 排除特性参数上下文（如 [Route("api/files")]）
        if (literal.Parent is AttributeArgumentSyntax)
        {
            return;
        }

        // 仅当字面量确实对应 HttpErrorCode 中的错误码时才报告，
        // 避免误报 HTTP 方法（"POST"）、环境变量名（"DROPBOX_HOME"）、UDP 协议串（"CLOUDPAN_DISCOVER"）等非错误码字符串
        var httpErrorCode = context.Compilation.GetTypeByMetadataName("CloudPan.Contract.HttpErrorCode");
        if (httpErrorCode == null || httpErrorCode.GetMembers(text).IsEmpty)
        {
            return;
        }

        Diagnostic diagnostic = Diagnostic.Create(Rule, literal.GetLocation(), text, text);
        context.ReportDiagnostic(diagnostic);
    }
}
