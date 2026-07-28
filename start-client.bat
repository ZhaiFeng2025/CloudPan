@echo off
chcp 65001 >nul
title CloudPan 客户端

echo ========================================
echo   CloudPan Client v0.1.0
echo ========================================
echo.
echo 正在编译...

cd /d "%~dp0"
dotnet build CloudPan.Client\CloudPan.Client.csproj -c Release >nul 2>&1
if %errorlevel% neq 0 (
    echo ❌ 编译失败
    pause
    exit /b 1
)

echo ✅ 编译完成
echo.

set SYNCDIR=%~dp0CloudPan_Client
if not exist "%SYNCDIR%" mkdir "%SYNCDIR%"

echo 📁 同步根: %SYNCDIR%
echo 🔗 服务端: http://localhost:8443
echo.
echo 托盘图标出现后：
echo   - 双击图标 → 打开同步状态窗口
echo   - 往 %SYNCDIR% 放文件 → 自动上传
echo   - 右键图标 → 暂停/退出
echo ========================================

dotnet run --project CloudPan.Client -- http://localhost:8443 "%SYNCDIR%"
pause
