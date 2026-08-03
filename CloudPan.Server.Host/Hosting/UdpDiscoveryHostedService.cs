using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using CloudPan.Contract;
using Microsoft.Extensions.Logging;

namespace CloudPan.Server.Host.Hosting;

/// <summary>
/// UDP 局域网发现服务：响应客户端 "CLOUDPAN_DISCOVER" 广播，返回服务端连接信息。
/// 从 Program.cs 提取为 IHostedService（R-A6）。
/// </summary>
public sealed class UdpDiscoveryHostedService : BackgroundService
{
    private readonly ILogger<UdpDiscoveryHostedService> _logger;
    private readonly int _httpPort;

    public UdpDiscoveryHostedService(ILogger<UdpDiscoveryHostedService> logger, int httpPort)
    {
        _logger = logger;
        _httpPort = httpPort;
    }

    /// <summary>读取程序集真实版本（AssemblyInformationalVersion，反映发布版本），供响应 version 字段。</summary>
    internal static string GetVersion() =>
        typeof(UdpDiscoveryHostedService).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? "0.0.0";

    /// <summary>优先返回活动网卡的局域网 IPv4（供客户端直连）；无可用地址时兜底主机名。</summary>
    internal static string GetLocalHost()
    {
        try
        {
            var ipv4 = NetworkInterface.GetAllNetworkInterfaces()
                .Where(ni => ni.OperationalStatus == OperationalStatus.Up)
                .Where(ni => ni.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .Where(ni => ni.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
                .SelectMany(ni => ni.GetIPProperties().UnicastAddresses)
                .Select(u => u.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a));
            if (ipv4 != null)
            {
                return ipv4.ToString();
            }
        }
        catch (Exception)
        {
            // 网卡枚举失败（如权限受限）时静默走主机名兜底
        }
        return Environment.MachineName;
    }

    /// <summary>构造发现响应 JSON：server 用局域网地址/主机名直连，name 恒为主机名，version 用程序集真实版本。</summary>
    internal static string BuildServerInfo(int httpPort, string host, string name, string version) =>
        "{\"server\":\"http://" + host + ":" + httpPort + "\",\"name\":\"" +
        name + "\",\"version\":\"" + version + "\"}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using UdpClient udp = new UdpClient(new IPEndPoint(IPAddress.Any, SpecPorts.UdpDiscoveryPort));
            string host = GetLocalHost();
            string name = Environment.MachineName;
            string version = GetVersion();
            byte[] serverInfo = Encoding.UTF8.GetBytes(BuildServerInfo(_httpPort, host, name, version));
            _logger.LogInformation("UDP 局域网发现服务已启动 (端口 {Port})", SpecPorts.UdpDiscoveryPort);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var result = await udp.ReceiveAsync(stoppingToken);
                    string msg = Encoding.UTF8.GetString(result.Buffer).Trim();
                    if (msg == "CLOUDPAN_DISCOVER")
                    {
                        await udp.SendAsync(serverInfo, serverInfo.Length, result.RemoteEndPoint);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "UDP 发现服务异常");
        }
    }
}
