using CloudPan.Server;
using CloudPan.Server.Data;
using CloudPan.Server.Services;
using CloudPan.Shared;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CloudPan.Server.Controllers;

/// <summary>
/// 健康检查 + 证书指纹端点。无需认证。
/// </summary>
[ApiController]
[EndpointAuth(AuthMode.Public)]
public class HealthController : ControllerBase
{
    private readonly IVersionService _versionService;
    private readonly IDbContextFactory<CloudPanDbContext> _dbFactory;
    private readonly IConfiguration _configuration;

    public HealthController(IVersionService versionService, IDbContextFactory<CloudPanDbContext> dbFactory, IConfiguration configuration)
    {
        _versionService = versionService;
        _dbFactory = dbFactory;
        _configuration = configuration;
    }

    /// <summary>GET /api/health — 服务健康状态（含磁盘、内存、DB 完整性）。</summary>
    [HttpGet("/api/health")]
    public async Task<IActionResult> GetHealth()
    {
        int version = await _versionService.GetCurrentVersionAsync();

        // 磁盘空间检查
        string syncRoot = _configuration.GetValue<string>("SyncRoot") ?? ".";
        string diskStatus = "ok";
        try
        {
            DriveInfo drive = new DriveInfo(Path.GetPathRoot(syncRoot)!);
            diskStatus = drive.AvailableFreeSpace < 100_000_000 ? "low" : "ok"; // <100MB 告警
        }
        catch { diskStatus = "unknown"; }

        // 内存
        long memMb = GC.GetTotalMemory(false) / 1_048_576;
        string memStatus = memMb > 500 ? "high" : "ok";

        // DB 完整性（PRAGMA integrity_check 返回 "ok" 单行；ExecuteSqlRawAsync 不读取结果，必须用 SqlQueryRaw）
        string dbStatus = "ok";
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var integrity = await db.Database.SqlQueryRaw<string>("PRAGMA integrity_check;").ToListAsync();
            dbStatus = integrity.Count == 1 && integrity[0] == "ok" ? "ok" : "error";
        }
        catch { dbStatus = "error"; }

        return Ok(new
        {
            Status = "ok",
            Version = "1.0.0",
            MaxVersion = version,
            SyncRoot = syncRoot,
            Disk = diskStatus,
            MemoryMb = memMb,
            MemoryStatus = memStatus,
            DbIntegrity = dbStatus,
            Uptime = (DateTime.UtcNow - System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime()).ToString(@"d\.hh\:mm"),
            Timestamp = DateTime.UtcNow.ToString("O")
        });
    }

    /// <summary>GET /api/version — 服务端版本（客户端自动更新检测用）。</summary>
    [HttpGet("/api/version")]
    public IActionResult GetVersion()
    {
        return Ok(new
        {
            version = "1.0.0",
            minClientVersion = "1.0.0",
            releaseNotes = "v1.0 正式发布——完整文件同步、版本历史、分享链接、回收站、管理面板",
            downloadUrl = "https://github.com/cloudpan/releases/latest"
        });
    }

    /// <summary>GET /pair — 设备配对帮助页面（显示完整 Token，与安装器/托盘一致）。</summary>
    [HttpGet("/pair")]
    [ApiExplorerSettings(IgnoreApi = true)]
    [EndpointAuth(AuthMode.Localhost)]
    public IActionResult PairingPage()
    {
        string hostName = Environment.MachineName;
        string syncRoot = _configuration.GetValue<string>("SyncRoot") ?? ".";
        string tokenPath = Path.Combine(syncRoot, ".cloudpan", "token.txt");

        // 从 token.txt 读取完整 Token（与安装器一致）
        string? token = SecretStore.ReadToken(syncRoot);
        string tokenTip = "";
        if (token == null)
        {
            token = "（Token 尚未生成，请等待服务首次启动）";
            tokenTip = "服务首次启动后，Token 将自动生成并保存在该文件中。";
        }
        else
        {
            // 每 16 字符一组用短横分隔
            token = string.Join("-",
                Enumerable.Range(0, (token.Length + 15) / 16)
                    .Select(i => token.Substring(i * 16, Math.Min(16, token.Length - i * 16))));
        }

        string scheme = _configuration.GetValue<bool>("Kestrel:Endpoints:Https:Enabled") ? "https" : "http";

        string html = $$"""
<!DOCTYPE html>
<html lang="zh-CN">
<head>
<meta charset="UTF-8">
<meta name="viewport" content="width=device-width, initial-scale=1.0">
<title>CloudPan 设备配对</title>
<style>
  body {
    font-family: -apple-system, "Microsoft YaHei", sans-serif;
    background: #f5f5f5;
    display: flex;
    justify-content: center;
    align-items: center;
    min-height: 100vh;
    margin: 0;
  }
  .card {
    background: #fff;
    border-radius: 12px;
    box-shadow: 0 4px 24px rgba(0,0,0,.1);
    padding: 40px;
    max-width: 520px;
    width: 90%;
  }
  h1 {
    font-size: 24px;
    margin: 0 0 24px;
    text-align: center;
    color: #333;
  }
  .field {
    margin-bottom: 20px;
  }
  .label {
    font-size: 13px;
    color: #888;
    margin-bottom: 6px;
  }
  .value {
    background: #f0f0f0;
    border-radius: 6px;
    padding: 10px 14px;
    font-family: "SFMono-Regular", Consolas, monospace;
    font-size: 15px;
    word-break: break-all;
    color: #222;
  }
  .token-value {
    background: #fffbe6;
    border: 1px solid #ffe58f;
    border-radius: 6px;
    padding: 14px;
    font-family: "SFMono-Regular", Consolas, monospace;
    font-size: 18px;
    word-break: break-all;
    color: #222;
    letter-spacing: 1px;
    user-select: all;
  }
  .hint {
    margin-top: 24px;
    padding: 12px 16px;
    background: #e8f4fd;
    border-radius: 8px;
    font-size: 14px;
    color: #1a5c8a;
    line-height: 1.5;
  }
  .footer {
    margin-top: 16px;
    padding: 8px 12px;
    background: #f6f8fa;
    border-radius: 6px;
    font-size: 12px;
    color: #888;
    text-align: center;
  }
</style>
</head>
<body>
<div class="card">
  <h1>CloudPan 设备配对</h1>

  <div class="field">
    <div class="label">服务端地址</div>
    <div class="value">{{scheme}}://{{hostName}}:{{SpecPorts.HttpPort}}</div>
  </div>

  <div class="field">
    <div class="label">家庭共享 Token（点击全选后复制）</div>
    <div class="token-value">{{token}}</div>
  </div>

  <div class="hint">请在客户端配置中输入以上地址和 Token，完成设备配对。Token 已按每16字符分组便于核对。</div>

  <div class="footer">
    Token 文件位置：{{tokenPath}}{{tokenTip}}
  </div>
</div>
</body>
</html>
""";
        return Content(html, "text/html; charset=utf-8");
    }

    /// <summary>GET /api/cert-fingerprint — 获取服务端证书 SHA-256 指纹（客户端 TOFU pinning）。</summary>
    [HttpGet("/api/cert-fingerprint")]
    [EndpointAuth(AuthMode.Token)]
    public async Task<IActionResult> GetCertFingerprint()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        string? fp = await db.AppConfigs
            .Where(c => c.Key == "cert_fingerprint")
            .Select(c => c.Value)
            .FirstOrDefaultAsync();
        return Ok(new { fingerprint = fp ?? "" });
    }
}
