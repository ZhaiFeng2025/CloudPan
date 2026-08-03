using System.Net;
using CloudPan.Client.Core.Services;
using Xunit;

namespace CloudPan.Tests.Client.Services;

/// <summary>
/// ErrorAttribution 异常归因单元测试——网络/权限/磁盘满三类异常归因正确，
/// AggregateException 递归解包全部内层异常（F-31）。
/// </summary>
public class ErrorAttributionTests
{
    [Fact]
    public void HttpRequestException_无状态码_归因为连接失败()
    {
        ErrorAttribution attribution = ErrorAttribution.FromException(new HttpRequestException("Connection refused"));

        Assert.Equal("无法连接到云盘服务", attribution.Message);
        Assert.Contains("台式机", attribution.NextStep);
        Assert.Contains("服务", attribution.NextStep);
    }

    [Fact]
    public void HttpRequestException_401_归因为Token失效需重新配置()
    {
        ErrorAttribution attribution = ErrorAttribution.FromException(
            new HttpRequestException("Unauthorized", null, HttpStatusCode.Unauthorized));

        Assert.Contains("凭证", attribution.Message);
        Assert.Contains("重新配置", attribution.NextStep);
    }

    [Fact]
    public void UnauthorizedAccessException_归因为权限不足()
    {
        ErrorAttribution attribution = ErrorAttribution.FromException(new UnauthorizedAccessException("Access denied"));

        Assert.Contains("权限", attribution.Message);
        Assert.Contains("文件夹", attribution.NextStep);
    }

    [Fact]
    public void IOException_磁盘满_归因为清理空间()
    {
        ErrorAttribution attribution = ErrorAttribution.FromException(new IOException("There is not enough space on the disk."));

        Assert.Contains("磁盘空间不足", attribution.Message);
        Assert.Contains("清理", attribution.NextStep);
    }

    [Fact]
    public void AggregateException_嵌套解包_取内层连接失败归因()
    {
        var inner = new HttpRequestException("Connection refused");
        var outer = new AggregateException(new AggregateException(inner));

        ErrorAttribution attribution = ErrorAttribution.FromException(outer);

        Assert.Equal("无法连接到云盘服务", attribution.Message);
        Assert.Contains("台式机", attribution.NextStep);
    }

    [Fact]
    public void AggregateException_多内层异常_取最具体归因()
    {
        // 内层 1 为未知类型、内层 2 为权限异常 → 应取权限归因（优先级更高）
        var unknown = new InvalidOperationException("unknown");
        var unauthorized = new UnauthorizedAccessException("access denied");
        var aggregate = new AggregateException(unknown, unauthorized);

        ErrorAttribution attribution = ErrorAttribution.FromException(aggregate);

        Assert.Contains("权限", attribution.Message);
    }

    [Fact]
    public void 未知异常_兜底为通用白话不露原始异常串()
    {
        ErrorAttribution attribution = ErrorAttribution.FromException(new InvalidOperationException("the original english message"));

        Assert.Contains("未知错误", attribution.Message);
        Assert.DoesNotContain("the original english message", attribution.Message);
    }

    [Fact]
    public void HttpRequestException_401_标记需要重新配置()
    {
        // F-34/T-034：401 归因标记 RequiresReconfiguration，供同步引擎判断「持续 401 = 服务端已变更」触发重配引导
        ErrorAttribution attribution = ErrorAttribution.FromException(
            new HttpRequestException("Unauthorized", null, HttpStatusCode.Unauthorized));

        Assert.True(attribution.RequiresReconfiguration);
    }

    [Fact]
    public void HttpRequestException_非401错误_不标记需要重新配置()
    {
        ErrorAttribution attribution = ErrorAttribution.FromException(
            new HttpRequestException("Not Found", null, HttpStatusCode.NotFound));

        Assert.False(attribution.RequiresReconfiguration);
    }
}
