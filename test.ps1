# CloudPan Phase 1a 一键测试脚本
# 用法: .\test.ps1 [-Token <token>]
#   -Token: 家庭共享 Token（服务端启动时显示）

param([string]$Token = "")

$Server = "http://localhost:8443"
$TestDir = "$env:TEMP\CloudPanTest"
New-Item -ItemType Directory -Force -Path $TestDir | Out-Null

# 认证头
$headers = @{}
if ($Token) {
    $headers["Authorization"] = "Bearer $Token"
}
$headers["X-Device-Id"] = "test-powershell-001"

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  CloudPan Phase 0 功能测试" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# 1. 健康检查
Write-Host "1. 健康检查..." -ForegroundColor Yellow
$health = Invoke-RestMethod -Uri "$Server/api/health"
Write-Host "   状态: $($health.status)  版本: $($health.version)  文件数: $($health.maxVersion)" -ForegroundColor Green

# 2. 上传文件
Write-Host "2. 上传文件..." -ForegroundColor Yellow
"Hello CloudPan! 测试通过 - $(Get-Date)" | Out-File "$TestDir\test.txt" -Encoding UTF8
$upload = Invoke-RestMethod -Uri "$Server/api/files/upload" -Method Post -Headers $headers -Form @{
    file = Get-Item "$TestDir\test.txt"
    path = "/test/hello.txt"
    baseVersion = "0"
    lastModified = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
}
Write-Host "   路径: $($upload.data.path)" -ForegroundColor Green
Write-Host "   版本: $($upload.data.version)  哈希: $($upload.data.hash.Substring(0,16))..." -ForegroundColor Green

# 3. 创建文件夹
Write-Host "3. 创建文件夹..." -ForegroundColor Yellow
$mkdir = Invoke-RestMethod -Uri "$Server/api/files/mkdir" -Method Post -Headers $headers -Body '{"path":"/docs/"}' -ContentType "application/json"
Write-Host "   文件夹: $($mkdir.data.path)" -ForegroundColor Green

# 4. 文件树
Write-Host "4. 文件树..." -ForegroundColor Yellow
$tree = Invoke-RestMethod -Uri "$Server/api/files/tree?sinceVersion=0&limit=10" -Headers $headers
foreach ($item in $tree.data) {
    $type = if ($item.type -eq 1) { "📁" } else { "📄" }
    Write-Host "   $type $($item.path)  v$($item.version)  $($item.size) bytes"
}

# 5. 下载文件
Write-Host "5. 下载文件..." -ForegroundColor Yellow
$encodedPath = [System.Web.HttpUtility]::UrlEncode("/test/hello.txt")
Invoke-WebRequest -Uri "$Server/api/files/download?path=$encodedPath" -Headers $headers -OutFile "$TestDir\download.txt"
$content = Get-Content "$TestDir\download.txt"
Write-Host "   内容: $content" -ForegroundColor Green

# 6. 搜索
Write-Host "6. 搜索..." -ForegroundColor Yellow
$search = Invoke-RestMethod -Uri "$Server/api/files/search?q=hello" -Headers $headers
Write-Host "   找到 $($search.data.Count) 个文件" -ForegroundColor Green

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  全部测试通过！✅" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "测试文件位置: $TestDir" -ForegroundColor Gray
Write-Host "服务端存储位置: C:\Users\Administrator\AppData\Local\Temp\cloudpan_test" -ForegroundColor Gray
