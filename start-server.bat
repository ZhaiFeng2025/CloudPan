@echo off
chcp 65001 >nul
title CloudPan 服务端

echo ========================================
echo   CloudPan Server v0.1.0
echo ========================================
echo.
echo 正在编译...

cd /d "%~dp0"
dotnet build CloudPan.Server\CloudPan.Server.csproj -c Release -o .build\server >nul 2>&1
if %errorlevel% neq 0 (
    echo ❌ 编译失败
    pause
    exit /b 1
)

echo ✅ 编译完成
echo.

set SYNCDIR=%~dp0CloudPanSync
if not exist "%SYNCDIR%" mkdir "%SYNCDIR%"

echo 📁 同步根: %SYNCDIR%
echo 🌐 监听: http://localhost:8443
echo.
echo 服务端启动后，保持此窗口打开，再双击 start-client.bat
echo ========================================

dotnet .build\server\CloudPan.Server.dll --SyncRoot "%SYNCDIR%"
pause
