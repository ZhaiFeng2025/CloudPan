using CloudPan.Contract;
using CloudPan.Server.Core;
using CloudPan.Server.Host;
using Microsoft.AspNetCore.Mvc;

namespace CloudPan.Server.Host.Controllers;

/// <summary>
/// 回收站 API——只做参数绑定与状态码适配，领域逻辑（列表/恢复/清空/移入）在 Server.Core ITrashService。
/// 浏览、恢复、清空已删除文件（保留 30 天）。
/// </summary>
[ApiController]
[Route("api/trash")]
[EndpointAuth(AuthMode.Token)]
public class TrashController : ControllerBase
{
    private readonly ITrashService _trash;

    public TrashController(ITrashService trash)
    {
        _trash = trash;
    }

    /// <summary>GET /api/trash — 列出回收站内容。</summary>
    [HttpGet]
    public async Task<IActionResult> ListTrash()
    {
        var items = await _trash.ListAsync();
        return Ok(new { data = items });
    }

    /// <summary>POST /api/trash/restore — 恢复文件。</summary>
    [HttpPost("restore")]
    public async Task<IActionResult> Restore([FromBody] RestoreTrashRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.MetaFileName))
        {
            return this.Error(HttpErrorCode.BAD_REQUEST, "metaFileName 参数缺失", "缺少回收站记录文件名");
        }

        var result = await _trash.RestoreAsync(request.MetaFileName);
        if (!result.Success)
        {
            return this.Error(result.Error!.Code, result.Error.Message, result.Error.UserMessage);
        }

        return Ok(new { data = new { restored = result.OriginalPath } });
    }

    /// <summary>DELETE /api/trash/empty — 清空回收站。</summary>
    [HttpDelete("empty")]
    public async Task<IActionResult> EmptyTrash()
    {
        await _trash.EmptyAsync();
        return Ok(new { data = "trash emptied" });
    }
}

/// <summary>从回收站恢复文件的请求，MetaFileName 为回收站元数据文件名。</summary>
public record RestoreTrashRequest(string MetaFileName);
