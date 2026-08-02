using CloudPan.Server;
using CloudPan.Server.Services;
using CloudPan.Shared;
using Microsoft.AspNetCore.Mvc;

namespace CloudPan.Server.Controllers;

/// <summary>
/// 设备管理 API——查看已注册设备列表。
/// 数据查询在 Server.Core IServerStatusService，本类只做 HTTP 适配。
/// </summary>
[ApiController]
[Route("api/devices")]
[EndpointAuth(AuthMode.Token)]
public class DevicesController : ControllerBase
{
    private readonly IServerStatusService _status;

    public DevicesController(IServerStatusService status)
    {
        _status = status;
    }

    /// <summary>
    /// GET /api/devices — 返回所有已注册设备。
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetDevices()
    {
        var devices = await _status.GetDevicesAsync();
        return Ok(new
        {
            data = devices.Select(d => new
            {
                deviceId = d.Id,
                name = d.Name,
                person = d.Person,
                lastSeen = d.LastSeen,
                online = d.Online,
                registeredAt = d.RegisteredAt
            })
        });
    }
}
