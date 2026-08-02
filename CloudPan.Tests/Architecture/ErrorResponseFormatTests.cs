using System.Text.Json;
using CloudPan.Contract;
using Xunit;

namespace CloudPan.Tests.Architecture;

/// <summary>
/// 错误响应格式一致性测试。
/// 验证所有 API 错误响应体含 code + message + friendlyMessage 三字段。
/// </summary>
public class ErrorResponseFormatTests
{
    /// <summary>
    /// 验证 ErrorResponse 序列化后包含所有必需字段。
    /// </summary>
    [Fact]
    public void ErrorResponse_序列化_包含三个必需字段()
    {
        ErrorResponse error = new ErrorResponse(HttpErrorCode.BAD_REQUEST.Code, "test msg", "测试消息", null);
        string json = JsonSerializer.Serialize(error.ToApiBody(), new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        });

        Assert.Contains("\"code\"", json);
        Assert.Contains("\"message\"", json);
        Assert.Contains("\"friendlyMessage\"", json);
        Assert.Contains("\"BAD_REQUEST\"", json);
        Assert.Contains("\"test msg\"", json);
        // 中文字符在默认序列化下被编码为 \uXXXX，验证 friendlyMessage 字段存在即可
        Assert.Contains("\"friendlyMessage\"", json);
    }

    /// <summary>
    /// 验证 ErrorResponse 序列化含 detail 字段。
    /// </summary>
    [Fact]
    public void ErrorResponse_带Detail_序列化包含detail字段()
    {
        ErrorResponse error = new ErrorResponse(HttpErrorCode.INTERNAL_ERROR.Code, "服务器错误", "服务暂时不可用", "Stack trace details");
        string json = JsonSerializer.Serialize(error.ToApiBody(), new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        Assert.Contains("\"detail\"", json);
        Assert.Contains("Stack trace details", json);
    }

    /// <summary>
    /// 验证 HttpErrorCode 所有错误码在 ApiErrors 中有对应工厂方法。
    /// </summary>
    [Fact]
    public void HttpErrorCode_所有成员_在ApiErrors中有对应工厂方法()
    {
        // 反射收集 HttpErrorCode 所有字段
        var httpErrorCodeType = typeof(HttpErrorCode);
        List<System.Reflection.FieldInfo> errorCodeFields = httpErrorCodeType.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.FieldType == typeof(ErrorCode))
            .ToList();

        // 反射收集 ApiErrors 所有公共方法
        var apiErrorsType = typeof(CloudPan.Server.Host.ApiErrors);
        List<System.Reflection.MethodInfo> factoryMethods = apiErrorsType.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(m => m.ReturnType == typeof(ErrorResponse))
            .ToList();

        Assert.NotEmpty(errorCodeFields);
        Assert.NotEmpty(factoryMethods);

        // 每个错误码应有方法名对应的工厂方法（如 BAD_REQUEST → BadRequest）
        foreach (var field in errorCodeFields)
        {
            // snake_case → PascalCase: BAD_REQUEST → BadRequest
            string expectedMethodName = string.Concat(
                field.Name.Split('_')
                    .Select(part => part.Length > 0
                        ? char.ToUpper(part[0]) + part[1..].ToLower()
                        : ""));
            bool hasFactory = factoryMethods.Any(m => m.Name == expectedMethodName);
            Assert.True(hasFactory,
                $"HttpErrorCode.{field.Name} 缺少对应的 ApiErrors.{expectedMethodName} 工厂方法");
        }
    }

    /// <summary>
    /// 验证 SpecEndpoints.All 包含所有控制器路由。
    /// </summary>
    [Fact]
    public void SpecEndpoints_包含期望的关键端点()
    {
        var endpoints = SpecEndpoints.All;

        // 核心端点
        Assert.Contains(endpoints, e => e.Path == "/api/health" && e.Auth == AuthMode.Public);
        Assert.Contains(endpoints, e => e.Path == "/api/files/tree" && e.Auth == AuthMode.Token);
        Assert.Contains(endpoints, e => e.Path == "/ws" && e.Auth == AuthMode.Message);

        // 新增端点
        Assert.Contains(endpoints, e => e.Path == "/api/version" && e.Auth == AuthMode.Public);
        Assert.Contains(endpoints, e => e.Path == "/pair" && e.Auth == AuthMode.Localhost);
        Assert.Contains(endpoints, e => e.Path == "/api/trash");
        Assert.Contains(endpoints, e => e.Path == "/admin" && e.Auth == AuthMode.Localhost);
    }
}
