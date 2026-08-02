# CloudPan Build Script - PowerShell 5.1 compatible
param([string]$OutDir = "$PSScriptRoot\publish")

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$dist = "$OutDir\win-x64"

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  CloudPan Build Script" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

if (Test-Path $OutDir) { Remove-Item -Recurse -Force $OutDir -ErrorAction SilentlyContinue }
New-Item -ItemType Directory -Path $dist -Force | Out-Null

# === Server ===
Write-Host "`n[1/3] Building Server..." -ForegroundColor Yellow
# 删除旧 exe 避免文件锁定
Remove-Item "$dist\Server\CloudPan.Server.exe" -Force -ErrorAction SilentlyContinue
dotnet publish "$root\CloudPan.Server.Host\CloudPan.Server.Host.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "$dist\Server" 2>&1

# Server install/uninstall scripts
Set-Content -Path "$dist\Server\install.bat" -Value @"
@echo off
chcp 65001 >nul
echo.
echo   CloudPan Server - Install / Upgrade
echo.
net session >nul 2>&1
if %errorlevel% neq 0 (
    echo Run as Administrator!
    pause
    exit /b 1
)
set NAME=CloudPanServer
sc stop %NAME% >nul 2>&1
sc delete %NAME% >nul 2>&1
sc create %NAME% binPath= "\"%~dp0CloudPan.Server.exe\"" start= auto >nul 2>&1
sc description %NAME% "CloudPan File Sync Service" >nul 2>&1
sc start %NAME% >nul 2>&1
echo.
echo   Server installed and started!
echo   Waiting for service to initialize...
timeout /t 4 /nobreak >nul
echo.
if exist "%USERPROFILE%\CloudPan\.cloudpan\token.txt" (
    echo   ========================================
    echo   Family Token:
    type "%USERPROFILE%\CloudPan\.cloudpan\token.txt"
    echo   ========================================
    echo   Token file: %USERPROFILE%\CloudPan\.cloudpan\token.txt
) else (
    echo   Service starting, Token will be generated shortly.
    echo   Check: %USERPROFILE%\CloudPan\.cloudpan\token.txt
)
echo   Manage: sc stop %NAME% / sc start %NAME%
pause
"@

Set-Content -Path "$dist\Server\uninstall.bat" -Value @"
@echo off
net session >nul 2>&1
if %errorlevel% neq 0 (
    echo Run as Administrator!
    pause
    exit /b 1
)
sc stop CloudPanServer >nul 2>&1
sc delete CloudPanServer >nul 2>&1
echo Server uninstalled.
echo.
set /p PURGE=Also delete sync folder? (y/n):
if /i "%PURGE%"=="y" (
    echo Deleting: %USERPROFILE%\CloudPan
    rmdir /s /q "%USERPROFILE%\CloudPan" 2>nul
    echo Sync folder deleted.
)
pause
"@

# Clean up unnecessary files
Get-ChildItem "$dist\Server" -Filter "*.pdb" -ErrorAction SilentlyContinue | Remove-Item -Force
Get-ChildItem "$dist\Server" -Filter "web.config" -ErrorAction SilentlyContinue | Remove-Item -Force
Get-ChildItem "$dist\Server" -Filter "*.staticwebassets*" -ErrorAction SilentlyContinue | Remove-Item -Force

# 保留 appsettings.json 作为运行时配置模板，删除开发环境配置
Remove-Item "$dist\Server\appsettings.Development.json" -ErrorAction SilentlyContinue
if (-not (Test-Path "$dist\Server\appsettings.json")) {
    Copy-Item "$root\CloudPan.Server.Host\appsettings.json" "$dist\Server\appsettings.json" -Force
}

Write-Host "  Server: OK" -ForegroundColor Green

# === Client ===
Write-Host "`n[2/3] Building Client..." -ForegroundColor Yellow
Remove-Item "$dist\Client\CloudPan.Client.exe" -Force -ErrorAction SilentlyContinue
dotnet publish "$root\CloudPan.Client.UI\CloudPan.Client.UI.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "$dist\Client" 2>&1

Set-Content -Path "$dist\Client\install.bat" -Value @"
@echo off
chcp 65001 >nul
echo.
echo   CloudPan Client - Install
echo.
set /p SERVER="Server URL (http://x.x.x.x:8443): "
set /p FOLDER="Sync folder [%USERPROFILE%\CloudPan]: "
if "%FOLDER%"=="" set FOLDER=%USERPROFILE%\CloudPan
set /p TOKEN="Family Token: "
if not exist "%FOLDER%" mkdir "%FOLDER%"

set EXE=%~dp0CloudPan.Client.exe
set LINK=%APPDATA%\Microsoft\Windows\Start Menu\Programs\CloudPan.lnk
set DESK=%USERPROFILE%\Desktop\CloudPan.lnk

powershell -Command "$w=New-Object -ComObject WScript.Shell;$s=$w.CreateShortcut('%LINK%');$s.TargetPath='%EXE%';$s.Arguments='%SERVER% \"%FOLDER%\" %TOKEN%';$s.WorkingDirectory='%CD%';$s.Save();$d=$w.CreateShortcut('%DESK%');$d.TargetPath='%EXE%';$d.Arguments='%SERVER% \"%FOLDER%\" %TOKEN%';$d.WorkingDirectory='%CD%';$d.Save()"

echo.
echo   Installed! Double-click desktop icon to start.
echo   Sync folder: %FOLDER%
pause
"@

Set-Content -Path "$dist\Client\uninstall.bat" -Value @"
@echo off
del /q "%APPDATA%\Microsoft\Windows\Start Menu\Programs\CloudPan.lnk" 2>nul
del /q "%USERPROFILE%\Desktop\CloudPan.lnk" 2>nul
del /q "%LOCALAPPDATA%\CloudPan\config.txt" 2>nul
del /q "%LOCALAPPDATA%\CloudPan\client-config.json" 2>nul
echo Client uninstalled.
echo.
set /p PURGE=Also delete sync folder? (y/n):
if /i "%PURGE%"=="y" (
    echo Deleting: %USERPROFILE%\CloudPan
    rmdir /s /q "%USERPROFILE%\CloudPan" 2>nul
    echo Sync folder deleted.
)
pause
"@

Get-ChildItem "$dist\Client" -Filter "*.pdb" -ErrorAction SilentlyContinue | Remove-Item -Force

Write-Host "  Client: OK" -ForegroundColor Green

# === Android ===
Write-Host "`n[3/3] Building Android APK..." -ForegroundColor Yellow
Push-Location "$root\CloudPan.Android"
if (Test-Path ".\gradlew.bat") {
    cmd /c ".\gradlew.bat assembleRelease" 2>&1
    $apk = Get-ChildItem -Recurse -Filter "*.apk" -File | Where-Object { $_.Name -like "*release*" } | Select-Object -First 1
    if ($apk) {
        Copy-Item $apk.FullName "$dist\CloudPan.apk" -Force
        Write-Host "  Android APK: OK ($([math]::Round($apk.Length/1MB,1)) MB)" -ForegroundColor Green
    } else {
        cmd /c ".\gradlew.bat assembleDebug" 2>&1
        $apk = Get-ChildItem -Recurse -Filter "*.apk" -File | Select-Object -First 1
        if ($apk) { Copy-Item $apk.FullName "$dist\CloudPan.apk" -Force; Write-Host "  Android APK: OK (debug)" -ForegroundColor Green }
    }
} else {
    Write-Host "  SKIP: gradlew.bat not found. Run 'gradle wrapper' in CloudPan.Android first." -ForegroundColor Yellow
    Write-Host "  Or install Android Studio and rebuild." -ForegroundColor Gray
}
Pop-Location

# === Setup Package（自解压安装包，非 MSIX） ===
$msixDir = "$dist\CloudPan-Setup"
New-Item -ItemType Directory -Path $msixDir -Force | Out-Null
Copy-Item "$dist\Client\CloudPan.Client.exe" $msixDir -Force
Copy-Item "$dist\Client\install.bat" $msixDir -Force
Copy-Item "$dist\Client\uninstall.bat" $msixDir -Force

# 创建自解压安装包脚本
Set-Content -Path "$msixDir\SETUP.bat" -Value @"
@echo off
title CloudPan Setup
echo.
echo   ========================================
echo     CloudPan File Sync - Setup
echo   ========================================
echo.
echo   Installing to: %LOCALAPPDATA%\CloudPan
echo.
xcopy /s /y "%~dp0*" "%LOCALAPPDATA%\CloudPan\" >nul
powershell -Command "$w=New-Object -ComObject WScript.Shell; ^
  $s=$w.CreateShortcut([Environment]::GetFolderPath('Desktop')+'\CloudPan.lnk'); ^
  $s.TargetPath='%LOCALAPPDATA%\CloudPan\CloudPan.Client.exe'; ^
  $s.WorkingDirectory='%LOCALAPPDATA%\CloudPan';$s.Save(); ^
  $m=$w.CreateShortcut([Environment]::GetFolderPath('Programs')+'\CloudPan.lnk'); ^
  $m.TargetPath='%LOCALAPPDATA%\CloudPan\CloudPan.Client.exe'; ^
  $m.WorkingDirectory='%LOCALAPPDATA%\CloudPan';$m.Save()"
echo.
echo   Installation complete!
echo.
echo   ========================================
echo   首次启动前，请确保 Server 已安装并记下 Token。
echo.
echo   步骤：
echo     1. 在台式机上以管理员权限运行 install.bat
echo     2. 在台式机上找到备份的 Token 文件：
echo        CloudPan 同步目录下 .cloudpan\token.txt
echo     3. 双击本机桌面 CloudPan 图标
echo     4. 输入台式机的 IP 地址（如 http://192.168.1.100:8443）
echo        和 Token 完成配置
echo   ========================================
echo.
echo   Double-click desktop icon to start.
pause
"@

Write-Host "  Setup: OK" -ForegroundColor Green

# === Summary ===
Write-Host "`n========================================" -ForegroundColor Green
Write-Host "  BUILD COMPLETE" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Green
Get-ChildItem $dist -Directory | ForEach-Object {
    Write-Host "`n  $($_.Name)" -ForegroundColor Yellow
    Get-ChildItem $_.FullName -File | ForEach-Object {
        $s = if ($_.Length -gt 1MB) { "$([math]::Round($_.Length/1MB,1)) MB" } else { "$([math]::Round($_.Length/1KB,0)) KB" }
        Write-Host "    $($_.Name)  ($s)"
    }
}

Write-Host "`n  Next steps:" -ForegroundColor Yellow
Write-Host "  1. Copy Server folder to desktop PC, run install.bat as Admin" -ForegroundColor White
Write-Host "  2. Run SETUP.bat from CloudPan-Setup folder (one-click install)" -ForegroundColor White
Write-Host "  3. Copy CloudPan.apk to Android phone" -ForegroundColor White
