@echo off
chcp 65001 >nul
cd /d "%~dp0"

set "EXE=%~dp0publish\win-x64\server\CloudPan.Server.exe"
set "SYNCDIR=%~dp0CloudPanSync"

if not exist "%SYNCDIR%" mkdir "%SYNCDIR%"

:: 检查已编译的可执行文件
if not exist "%EXE%" (
    echo ========================================
    echo   CloudPan Server v1.0.0
    echo ========================================
    echo.
    echo   [错误] 未找到可执行文件：CloudPan.Server.exe
    echo.
    echo   原因：尚未编译发布。
    echo.
    echo   解决方法（任选其一）：
    echo     1. 运行 publish.ps1（推荐）
    echo        ^> powershell -ExecutionPolicy Bypass -File publish.ps1
    echo.
    echo     2. 或在 Visual Studio 中右键 CloudPan.Server ^> 发布
    echo.
    pause
    exit /b 1
)

echo ========================================
echo   CloudPan Server v1.0.0
echo   同步目录: %SYNCDIR%
echo   服务地址: http://localhost:8443
echo   按 Ctrl+C 停止服务
echo ========================================
echo.

"%EXE%" --SyncRoot "%SYNCDIR%"

echo.
echo 服务已停止。
pause
