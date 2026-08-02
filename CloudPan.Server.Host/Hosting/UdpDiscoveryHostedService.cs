using System.Net;
using System.Net.Sockets;
using System.Text;
using CloudPan.Shared;
using Microsoft.Extensions.Logging;

namespace CloudPan.Server.Hosting;

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

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using UdpClient udp = new UdpClient(new IPEndPoint(IPAddress.Any, SpecPorts.UdpDiscoveryPort));
            byte[] serverInfo = Encoding.UTF8.GetBytes(
                "{\"server\":\"http://" + Environment.MachineName + ":" + _httpPort + "\",\"name\":\"" +
                Environment.MachineName + "\",\"version\":\"0.2.0\"}");
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
