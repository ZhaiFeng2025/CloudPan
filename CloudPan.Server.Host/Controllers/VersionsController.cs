using CloudPan.Contract;
using CloudPan.Server.Core;
using CloudPan.Server.Host;
using Microsoft.AspNetCore.Mvc;

namespace CloudPan.Server.Host.Controllers;

/// <summary>
/// 版本历史 API——只做参数绑定与状态码适配，领域逻辑（列表/回滚事务）在 Server.Core IVersionHistoryService。
/// </summary>
[ApiController]
[EndpointAuth(AuthMode.Token)]
public class VersionsController : ControllerBase
{
    private readonly IVersionHistoryService _versions;
    private readonly IWebSocketHandler _wsHandler;

    public VersionsController(IVersionHistoryService versions, IWebSocketHandler wsHandler)
    {
        _versions = versions;
        _wsHandler = wsHandler;
    }

    /// <summary>
    /// GET /api/versions?path=... — 获取文件的所有历史版本。
    /// </summary>
    [HttpGet(SpecRoutes.Versions)]
    public async Task<IActionResult> GetVersions([FromQuery] string path, [FromQuery] int limit = 10)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return this.Error(HttpErrorCode.BAD_REQUEST, "path 参数缺失", "缺少文件路径参数");
        }

        var versions = await _versions.GetVersionsAsync(path, limit);
        return Ok(new VersionListResponse(versions.ToArray()));
    }

    /// <summary>
    /// POST /api/versions/restore — 回滚到指定历史版本。
    /// 回滚本身会先存档当前版本，再用历史文件覆盖。
    /// </summary>
    [HttpPost(SpecRoutes.VersionsRestore)]
    public async Task<IActionResult> Restore([FromBody] RestoreRequestDto request)
    {
        if (string.IsNullOrWhiteSpace(request.FilePath))
        {
            return this.Error(HttpErrorCode.BAD_REQUEST, "filePath 参数缺失", "缺少文件路径参数");
        }

        string deviceId = HttpContext.Items["DeviceId"] as string ?? "unknown";
        var result = await _versions.RestoreAsync(request.FilePath, request.Version, deviceId);
        if (!result.Success)
        {
            return this.Error(result.Error!.Code, result.Error.Message, result.Error.UserMessage);
        }

        // WebSocket 广播（通知其他设备文件已变更）
        await _wsHandler.BroadcastFileChangedAsync(request.FilePath, result.Version!.Value, deviceId);

        return Ok(new VersionRestoreResponse(new VersionRestoreData(
            result.Path!,
            result.Version!.Value,
            result.Hash!,
            result.Size!.Value,
            result.RestoredFromVersion)));
    }
}
