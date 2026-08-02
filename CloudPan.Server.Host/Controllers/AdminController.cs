using CloudPan.Server;
using CloudPan.Server.Data;
using CloudPan.Shared;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CloudPan.Server.Controllers;

/// <summary>
/// Web 管理面板——localhost 只读视图。
/// 绑定 127.0.0.1 / ::1，公网不可达。
/// </summary>
[ApiController]
[EndpointAuth(AuthMode.Localhost)]
public class AdminController : ControllerBase
{
    private readonly IDbContextFactory<CloudPanDbContext> _dbFactory;

    public AdminController(IDbContextFactory<CloudPanDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    /// <summary>GET /admin — 管理面板主页（仅 localhost）。</summary>
    [HttpGet("/admin")]
    public IActionResult Dashboard()
    {
        return Content(Html, "text/html; charset=utf-8");
    }

    /// <summary>GET /admin/api/files — 文件列表数据。</summary>
    [HttpGet("/admin/api/files")]
    public async Task<IActionResult> GetFiles([FromQuery] string? path = null, [FromQuery] int limit = 200)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var query = db.FileEntries.AsQueryable();
        if (!string.IsNullOrEmpty(path))
        {
            query = query.Where(f => f.Path.StartsWith(path));
        }

        var items = await query
            .OrderBy(f => f.Path)
            .Take(Math.Min(limit, 1000))
            .Select(f => new
            {
                f.Path,
                f.Type,
                f.CurrentHash,
                f.CurrentSize,
                f.Version,
                f.State,
                f.LastModified
            })
            .ToListAsync();
        return Ok(new { data = items });
    }

    /// <summary>GET /admin/api/devices — 设备列表数据。</summary>
    [HttpGet("/admin/api/devices")]
    public async Task<IActionResult> GetDevices()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var items = await db.Devices
            .OrderByDescending(d => d.LastSeen)
            .Select(d => new
            {
                d.Id,
                d.Name,
                d.Person,
                d.LastSeen,
                d.Online,
                d.RegisteredAt
            })
            .ToListAsync();
        return Ok(new { data = items });
    }

    /// <summary>GET /admin/api/logs — 同步日志数据。</summary>
    [HttpGet("/admin/api/logs")]
    public async Task<IActionResult> GetLogs([FromQuery] int limit = 100)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var items = await db.SyncLogs
            .OrderByDescending(l => l.CreatedAt)
            .Take(Math.Min(limit, 500))
            .Select(l => new
            {
                l.Id,
                l.FilePath,
                l.Operation,
                l.DeviceId,
                l.Result,
                l.Details,
                l.CreatedAt
            })
            .ToListAsync();
        return Ok(new { data = items });
    }

    /// <summary>GET /admin/api/stats — 聚合统计（真实总数）。</summary>
    [HttpGet("/admin/api/stats")]
    public async Task<IActionResult> GetStats()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        int fileCount = await db.FileEntries.CountAsync();
        int deviceCount = await db.Devices.CountAsync();
        int onlineCount = await db.Devices.CountAsync(d => d.Online == 1);
        int logCount = await db.SyncLogs.CountAsync();
        return Ok(new { fileCount, deviceCount, onlineDeviceCount = onlineCount, logCount });
    }

    // ============================================================
    // 内联 HTML（无外部依赖，自包含）
    // ============================================================

    private const string Html = @"<!DOCTYPE html>
<html lang=""zh-CN"">
<head>
<meta charset=""UTF-8"">
<meta name=""viewport"" content=""width=device-width,initial-scale=1"">
<title>CloudPan 管理面板</title>
<style>
:root{--bg:#f5f5f4;--card:#fff;--text:#1c1917;--muted:#78716c;--border:#e7e5e4;--accent:#2563eb}
*{margin:0;padding:0;box-sizing:border-box}
body{font-family:-apple-system,'PingFang SC','Microsoft YaHei',sans-serif;background:var(--bg);color:var(--text);line-height:1.6;font-size:14px}
.container{max-width:1200px;margin:0 auto;padding:24px}
h1{font-size:22px;font-weight:700;margin-bottom:20px;display:flex;align-items:center;gap:10px}
h1 span{font-size:13px;font-weight:400;color:var(--muted);background:var(--border);padding:2px 8px;border-radius:4px}
.tabs{display:flex;gap:4px;margin-bottom:20px}
.tab{padding:8px 20px;border:none;background:var(--card);border:1px solid var(--border);border-radius:8px 8px 0 0;cursor:pointer;font-size:14px;color:var(--muted)}
.tab.active{background:var(--card);border-bottom-color:var(--card);color:var(--text);font-weight:600}
.card{background:var(--card);border:1px solid var(--border);border-radius:0 8px 8px 8px;padding:16px;overflow-x:auto}
table{width:100%;border-collapse:collapse}
th{text-align:left;padding:8px 12px;font-size:12px;color:var(--muted);border-bottom:2px solid var(--border);white-space:nowrap}
td{padding:6px 12px;border-bottom:1px solid var(--border);font-size:13px;max-width:400px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
tr:hover{background:#fafaf9}
.badge{display:inline-block;padding:1px 8px;border-radius:10px;font-size:11px;font-weight:600}
.badge-online{background:#dcfce7;color:#166534}
.badge-offline{background:#fef3c7;color:#92400e}
.badge-dir{background:#dbeafe;color:#1e40af}
.badge-file{background:#f1f5f9;color:#475569}
.badge-success{background:#dcfce7;color:#166534}
.badge-conflict{background:#fef3c7;color:#92400e}
.badge-error{background:#fee2e2;color:#991b1b}
.stats{display:grid;grid-template-columns:repeat(auto-fit,minmax(180px,1fr));gap:12px;margin-bottom:20px}
.stat{padding:16px;background:var(--card);border:1px solid var(--border);border-radius:8px}
.stat-value{font-size:28px;font-weight:700}
.stat-label{font-size:12px;color:var(--muted);margin-top:4px}
.loading{text-align:center;padding:40px;color:var(--muted)}
</style>
</head>
<body>
<div class=""container"">
<h1>CloudPan <span>管理面板</span></h1>

<div class=""stats"" id=""stats""></div>

<div class=""tabs"">
<button class=""tab active"" onclick=""showTab('files')"">📁 文件列表</button>
<button class=""tab"" onclick=""showTab('devices')"">📱 设备列表</button>
<button class=""tab"" onclick=""showTab('logs')"">📋 同步日志</button>
</div>

<div class=""card"" id=""content""><div class=""loading"">加载中...</div></div>
</div>

<script>
let currentTab='files';

async function fetchData(url){
 const r=await fetch(url);
 if(!r.ok) return {data:[]};
 return await r.json();
}

function showTab(tab){
 currentTab=tab;
 document.querySelectorAll('.tab').forEach(t=>t.classList.remove('active'));
 event.target.classList.add('active');
 loadTab();
}

async function loadTab(){
 const c=document.getElementById('content');
 c.innerHTML='<div class=""loading"">加载中...</div>';

 if(currentTab==='files'){
  const d=await fetchData('/admin/api/files');
  c.innerHTML=`<table><thead><tr><th>路径</th><th>类型</th><th>大小</th><th>版本</th><th>状态</th><th>修改时间</th></tr></thead><tbody>${d.data.map(f=>`<tr>
   <td title=""${f.path}"">${f.path}</td>
   <td><span class=""badge ${f.type===1?'badge-dir':'badge-file'}"">${f.type===1?'目录':'文件'}</span></td>
   <td>${formatSize(f.currentSize)}</td><td>${f.version}</td><td>${f.state}</td><td>${f.lastModified?.substring(0,19)||''}</td>
  </tr>`).join('')}</tbody></table>`;
 }else if(currentTab==='devices'){
  const d=await fetchData('/admin/api/devices');
  c.innerHTML=`<table><thead><tr><th>设备ID</th><th>名称</th><th>在线</th><th>最后在线</th><th>注册时间</th></tr></thead><tbody>${d.data.map(d=>`<tr>
   <td>${d.id}</td><td>${d.name}</td>
   <td><span class=""badge ${d.online?'badge-online':'badge-offline'}"">${d.online?'在线':'离线'}</span></td>
   <td>${d.lastSeen?.substring(0,19)||''}</td><td>${d.registeredAt?.substring(0,19)||''}</td>
  </tr>`).join('')}</tbody></table>`;
 }else if(currentTab==='logs'){
  const d=await fetchData('/admin/api/logs?limit=200');
  c.innerHTML=`<table><thead><tr><th>时间</th><th>操作</th><th>文件</th><th>设备</th><th>结果</th><th>详情</th></tr></thead><tbody>${d.data.map(l=>`<tr>
   <td>${l.createdAt?.substring(0,19)||''}</td><td>${['上传','下载','删除','重命名','回滚'][l.operation]||l.operation}</td>
   <td title=""${l.filePath}"">${l.filePath}</td>
   <td>${l.deviceId?.substring(0,8)||''}</td>
   <td><span class=""badge ${l.result===0?'badge-success':l.result===1?'badge-conflict':'badge-error'}"">${['成功','冲突','错误'][l.result]||l.result}</span></td>
   <td>${l.details||''}</td></tr>`).join('')}</tbody></table>`;
 }
}

function formatSize(b){if(!b||b===0)return'0 B';if(b>=1048576)return(b/1048576).toFixed(1)+' MB';if(b>=1024)return(b/1024).toFixed(0)+' KB';return b+' B';}

async function loadStats(){
 const st=await fetchData('/admin/api/stats');
 if(!st.fileCount) return;
 document.getElementById('stats').innerHTML=`
  <div class=""stat""><div class=""stat-value"">${st.fileCount}</div><div class=""stat-label"">文件（显示前200条）</div></div>
  <div class=""stat""><div class=""stat-value"">${st.deviceCount}</div><div class=""stat-label"">设备总数（${st.onlineDeviceCount} 在线）</div></div>
  <div class=""stat""><div class=""stat-value"">${st.logCount}</div><div class=""stat-label"">日志条目</div></div>`;
}

loadStats();
loadTab();
setInterval(loadStats, 10000);
setInterval(loadTab, 10000);
</script>
</body>
</html>";
}
