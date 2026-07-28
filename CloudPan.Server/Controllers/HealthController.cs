using Microsoft.AspNetCore.Mvc;
using CloudPan.Server.Services;

namespace CloudPan.Server.Controllers;

/// <summary>
/// 健康检查端点。无需认证，Phase 0 不校验 Token。
/// </summary>
[ApiController]
[Route("api/health")]
public class HealthController : ControllerBase
{
    private readonly VersionService _versionService;

    public HealthController(VersionService versionService)
    {
        _versionService = versionService;
    }

    /// <summary>
    /// GET /api/health — 服务健康状态。
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetHealth()
    {
        var version = await _versionService.GetCurrentVersionAsync();
        return Ok(new
        {
            Status = "ok",
            Version = "0.1.0",
            MaxVersion = version,
            Timestamp = DateTime.UtcNow.ToString("O")
        });
    }
}
