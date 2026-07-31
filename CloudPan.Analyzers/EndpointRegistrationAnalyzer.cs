using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace CloudPan.Analyzers;

/// <summary>
/// CP100: 检测未在 shared-spec.json 注册的控制器端点。
/// 拼接类 [Route] 与方法 [HttpGet]/[HttpPost]/[HttpDelete]/[HttpPut] 得到完整路由，
/// 模板参数归一化（{shareId} → {x}）后与契约端点表比对，未找到即报告。
/// 排除 Generated/ 目录与 Analyzer 项目自身。
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class EndpointRegistrationAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "CP100";

    private static readonly string Title = "端点未在契约中注册";
    private static readonly string MessageFormat = "端点 {0} {1} 未在 shared-spec.json 注册";
    private static readonly string Description = "所有控制器端点必须在 shared-spec.json → api.endpoints 中注册，契约是唯一事实来源.";
    private static readonly string HelpLink = "https://github.com/cloudpan/spec#api.endpoints";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId, Title, MessageFormat,
        "Contract", DiagnosticSeverity.Error,
        isEnabledByDefault: true, Description, HelpLink);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeMethod, SyntaxKind.MethodDeclaration);
    }

    private static void AnalyzeMethod(SyntaxNodeAnalysisContext context)
    {
        if (AnalyzerSupport.ShouldSkip(context.Node.SyntaxTree.FilePath))
        {
            return;
        }

        MethodDeclarationSyntax method = (MethodDeclarationSyntax)context.Node;
        if (!AnalyzerSupport.InController(context, method))
        {
            return;
        }

        ImmutableArray<SpecEndpoint> endpoints = SpecEndpoints.Get(context.Options);
        if (endpoints.IsEmpty)
        {
            return; // 未找到契约文件时静默（例如 IDE 会话中 AdditionalFiles 缺失）
        }

        foreach (EndpointActionInfo action in EndpointRouteHelper.GetEndpoints(method))
        {
            if (SpecEndpoints.Find(endpoints, action.Method, action.NormalizedPath) is null)
            {
                Diagnostic diagnostic = Diagnostic.Create(
                    Rule, action.VerbAttribute.GetLocation(), action.Method, action.NormalizedPath);
                context.ReportDiagnostic(diagnostic);
            }
        }
    }
}
