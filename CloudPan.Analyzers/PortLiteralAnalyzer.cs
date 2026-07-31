using System;
using System.Collections.Immutable;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace CloudPan.Analyzers;

/// <summary>
/// CP201: 检测硬编码端口字面量（默认 8443 / 8450）。
/// 端口由 shared-spec.json → config.httpPort / config.udpDiscoveryPort 定义（契约唯一事实来源），
/// 代码中应引用生成的 SpecPorts.HttpPort / SpecPorts.UdpDiscoveryPort 常量。
/// 端口值从 AdditionalFiles 中的 shared-spec.json 读取，解析失败时回退默认值。
/// 排除 Generated/ 目录、appsettings.json 相关文件与 Analyzer 项目自身。
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PortLiteralAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "CP201";

    private static readonly string Title = "硬编码端口字面量";
    private static readonly string MessageFormat = "端口字面量 {0} 请引用 SpecPorts.{1} 常量（契约唯一来源 shared-spec.json）";
    private static readonly string Description = "端口值必须引用生成的 SpecPorts.HttpPort / SpecPorts.UdpDiscoveryPort，而非手写字面量，确保与 shared-spec.json 契约一致.";
    private static readonly string HelpLink = "https://github.com/cloudpan/spec#config";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId, Title, MessageFormat,
        "Contract", DiagnosticSeverity.Warning,
        isEnabledByDefault: true, Description, HelpLink);

    // spec 中 config.httpPort / config.udpDiscoveryPort 缺失或解析失败时的回退值
    private const int DefaultHttpPort = 8443;
    private const int DefaultUdpDiscoveryPort = 8450;

    // 只匹配「键 + 冒号 + 数字」形态；_ref: "config.httpPort" 与注释串（"httpPort": "说明"）不会命中
    private static readonly Regex HttpPortRegex = new(@"""httpPort""\s*:\s*(\d+)", RegexOptions.Compiled);
    private static readonly Regex UdpPortRegex = new(@"""udpDiscoveryPort""\s*:\s*(\d+)", RegexOptions.Compiled);

    /// <summary>端口值 → SpecPorts 常量名。</summary>
    private static readonly ImmutableDictionary<int, string> Defaults =
        ImmutableDictionary<int, string>.Empty
            .Add(DefaultHttpPort, "HttpPort")
            .Add(DefaultUdpDiscoveryPort, "UdpDiscoveryPort");

    private static readonly object SyncRoot = new();
    private static string? _cachedSource;
    private static ImmutableDictionary<int, string> _cache = Defaults;

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeLiteral, SyntaxKind.NumericLiteralExpression);
    }

    private void AnalyzeLiteral(SyntaxNodeAnalysisContext context)
    {
        // 排除 Generated/ 目录、appsettings.json 相关文件与 Analyzer 项目自身
        if (AnalyzerSupport.ShouldSkip(context.Node.SyntaxTree.FilePath)
            || IsAppSettingsFile(context.Node.SyntaxTree.FilePath))
        {
            return;
        }

        LiteralExpressionSyntax literal = (LiteralExpressionSyntax)context.Node;
        long value;
        try
        {
            value = Convert.ToInt64(literal.Token.Value);
        }
        catch
        {
            return; // 非整数数值（浮点/超大整型）直接跳过
        }

        // 端口表键为 int，超范围字面量（如 ulong.MaxValue 经 long 回绕）不可能命中
        if (value < int.MinValue || value > int.MaxValue)
        {
            return;
        }

        if (!GetPorts(context.Options).TryGetValue((int)value, out string? constantName))
        {
            return;
        }

        Diagnostic diagnostic = Diagnostic.Create(Rule, literal.GetLocation(), value, constantName);
        context.ReportDiagnostic(diagnostic);
    }

    /// <summary>appsettings.json / appsettings.Development.json 等配置文件不参与检查。</summary>
    private static bool IsAppSettingsFile(string filePath)
        => Path.GetFileName(filePath).IndexOf("appsettings", StringComparison.OrdinalIgnoreCase) >= 0;

    /// <summary>从 AdditionalFiles 读取 shared-spec.json 并按来源内容缓存端口表。</summary>
    private static ImmutableDictionary<int, string> GetPorts(AnalyzerOptions options)
    {
        AdditionalText? spec = null;
        foreach (AdditionalText file in options.AdditionalFiles)
        {
            if (Path.GetFileName(file.Path) == "shared-spec.json")
            {
                spec = file;
                break;
            }
        }
        if (spec is null)
        {
            return Defaults;
        }

        string source = spec.GetText()?.ToString() ?? string.Empty;
        lock (SyncRoot)
        {
            if (_cachedSource != source)
            {
                _cachedSource = source;
                _cache = Parse(source);
            }
            return _cache;
        }
    }

    /// <summary>解析 config.httpPort / config.udpDiscoveryPort；匹配失败时回退默认值。</summary>
    private static ImmutableDictionary<int, string> Parse(string source)
    {
        var builder = ImmutableDictionary.CreateBuilder<int, string>();
        AddPort(builder, HttpPortRegex.Match(source), "HttpPort", DefaultHttpPort);
        AddPort(builder, UdpPortRegex.Match(source), "UdpDiscoveryPort", DefaultUdpDiscoveryPort);
        return builder.ToImmutable();
    }

    private static void AddPort(ImmutableDictionary<int, string>.Builder builder, Match match, string constantName, int fallback)
    {
        int port = match.Success && int.TryParse(match.Groups[1].Value, out int parsed) ? parsed : fallback;
        builder[port] = constantName;
    }
}
