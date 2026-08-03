using CloudPan.Contract;
using CloudPan.Server.Core;
using CloudPan.Server.Host;
using Microsoft.AspNetCore.Mvc;

namespace CloudPan.Server.Host.Controllers;

/// <summary>
/// 设备管理 API——查看已注册设备列表。
/// 数据查询在 Server.Core IServerStatusService，本类只做 HTTP 适配。
/// </summary>
[ApiController]
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
    [HttpGet(SpecRoutes.Devices)]
    public async Task<IActionResult> GetDevices()
    {
        var devices = await _status.GetDevicesAsync();
        return Ok(new DevicesResponse(
            devices.Select(d => new DeviceItem(d.Id, d.Name, d.Person, d.LastSeen, d.Online, d.RegisteredAt)).ToArray()));
    }
}
