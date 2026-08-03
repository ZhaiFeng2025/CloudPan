using System.Net;
using System.Reflection;
using System.Text.Json;
using CloudPan.Contract;
using CloudPan.Server.Host.Hosting;
using Xunit;

namespace CloudPan.Tests.Server.Hosting;

/// <summary>
/// UdpDiscoveryHostedService 单元测试——发现响应版本与主机来源（T-035，F-35）。
/// 内部静态方法经 InternalsVisibleTo 直接测试，脱离网络发送。
/// </summary>
public class UdpDiscoveryHostedServiceTests
{
    private static string AssemblyVersion =>
        typeof(UdpDiscoveryHostedService).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? "0.0.0";

    [Fact]
    public void GetVersion_等于程序集InformationalVersion()
    {
        Assert.Equal(AssemblyVersion, UdpDiscoveryHostedService.GetVersion());
    }

    [Fact]
    public void GetLocalHost_返回局域网IP或MachineName兜底()
    {
        string host = UdpDiscoveryHostedService.GetLocalHost();

        bool isIp = IPAddress.TryParse(host, out _);
        Assert.True(isIp || host == Environment.MachineName,
            $"host='{host}' 既不是局域网 IPv4，也不是 MachineName 兜底");
    }

    [Fact]
    public void BuildServerInfo_响应JSON版本与程序集一致且host可取()
    {
        string json = UdpDiscoveryHostedService.BuildServerInfo(
            SpecPorts.HttpPort, UdpDiscoveryHostedService.GetLocalHost(),
            Environment.MachineName, UdpDiscoveryHostedService.GetVersion());

        using var doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        // version 与程序集 InformationalVersion 一致
        Assert.Equal(AssemblyVersion, root.GetProperty("version").GetString());

        // server 主机为局域网 IP 或主机名兜底，可直接构造 URI
        string serverUrl = root.GetProperty("server").GetString()!;
        var uri = new Uri(serverUrl);
        Assert.Equal(UdpDiscoveryHostedService.GetLocalHost(), uri.Host, ignoreCase: true);
        Assert.Equal(SpecPorts.HttpPort, uri.Port);

        // name 恒为主机名
        Assert.Equal(Environment.MachineName, root.GetProperty("name").GetString());
    }
}
