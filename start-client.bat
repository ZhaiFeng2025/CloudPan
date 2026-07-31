@echo off
chcp 65001 >nul
cd /d "%~dp0"

set "EXE=%~dp0publish\dist\Client\CloudPan.Client.exe"
set "SYNCDIR=%~dp0CloudPan_Client"

if not exist "%SYNCDIR%" mkdir "%SYNCDIR%"

:: 检查已编译的可执行文件
if not exist "%EXE%" (
    echo ========================================
    echo   CloudPan Client v1.0.0
    echo ========================================
    echo.
    echo   [错误] 未找到可执行文件：CloudPan.Client.exe
    echo.
    echo   原因：尚未编译发布。
    echo.
    echo   解决方法（任选其一）：
    echo     1. 运行 publish.ps1（推荐）
    echo        ^> powershell -ExecutionPolicy Bypass -File publish.ps1
    echo.
    echo     2. 或在 Visual Studio 中右键 CloudPan.Client ^> 发布
    echo.
    pause
    exit /b 1
)

echo ========================================
echo   CloudPan Client v1.0.0
echo   同步目录: %SYNCDIR%
echo   服务地址: http://localhost:8443
echo.
echo   用法: start-client.bat [serverUrl] [syncRoot] [token]
echo.
echo   如果没有 Token：
echo     服务端首次运行后，Token 保存在同步目录的
echo     .cloudpan\token.txt 文件中
echo ========================================
echo.

"%EXE%" http://localhost:8443 "%SYNCDIR%" "%CLOUDPAN_TOKEN%"

pause
