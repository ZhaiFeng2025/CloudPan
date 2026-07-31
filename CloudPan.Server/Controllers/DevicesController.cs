using CloudPan.Server;
using CloudPan.Server.Data;
using CloudPan.Shared;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CloudPan.Server.Controllers;

/// <summary>
/// 设备管理 API——查看已注册设备列表。
/// </summary>
[ApiController]
[Route("api/devices")]
[EndpointAuth(AuthMode.Token)]
public class DevicesController : ControllerBase
{
    private readonly IDbContextFactory<CloudPanDbContext> _dbFactory;

    public DevicesController(IDbContextFactory<CloudPanDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    /// <summary>
    /// GET /api/devices — 返回所有已注册设备。
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetDevices()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var devices = await db.Devices
            .OrderByDescending(d => d.LastSeen)
            .Select(d => new
            {
                deviceId = d.Id,
                name = d.Name,
                person = d.Person,
                lastSeen = d.LastSeen,
                online = d.Online,
                registeredAt = d.RegisteredAt
            })
            .ToListAsync();

        return Ok(new { data = devices });
    }
}
