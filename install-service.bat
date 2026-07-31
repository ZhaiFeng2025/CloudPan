@echo off
chcp 65001 >nul
title CloudPan Server v1.0.0 — Windows Service 安装

REM CloudPan Server — Windows Service 安装脚本（需管理员权限）
set SERVICE_NAME=CloudPanServer
set DISPLAY_NAME="CloudPan 文件同步服务"
set DESCRIPTION="自托管家庭文件同步系统——后台服务"
set BINPATH=%~dp0publish\dist\Server\CloudPan.Server.exe
set SYNCDIR=%USERPROFILE%\CloudPan

REM 检查管理员权限
net session >nul 2>&1
if %errorlevel% neq 0 (
    echo 需要管理员权限！请右键以管理员身份运行。
    pause
    exit /b 1
)

REM 检查可执行文件是否存在
if not exist "%BINPATH%" (
    echo ========================================
    echo   CloudPan Server v1.0.0
    echo ========================================
    echo.
    echo   [错误] 未找到可执行文件：CloudPan.Server.exe
    echo.
    echo   请先运行 publish.ps1 编译发布：
    echo     powershell -ExecutionPolicy Bypass -File publish.ps1
    echo.
    pause
    exit /b 1
)

echo ========================================
echo   CloudPan Server v1.0.0
echo   正在安装 Windows 服务...
echo ========================================
echo.

REM 如果服务已存在，先停止并删除
sc stop %SERVICE_NAME% >nul 2>&1
sc delete %SERVICE_NAME% >nul 2>&1

REM 创建服务
sc create %SERVICE_NAME% ^
    binPath= "\"%BINPATH%\" --SyncRoot \"%SYNCDIR%\"" ^
    DisplayName= %DISPLAY_NAME% ^
    start= auto

sc description %SERVICE_NAME% %DESCRIPTION%

REM M-04: 崩溃自动恢复（24小时内重启3次：5秒、10秒、60秒）
sc failure %SERVICE_NAME% reset=86400 actions=restart/5000/restart/10000/restart/60000

sc start %SERVICE_NAME%

echo.
echo ========================================
echo   CloudPan Server v1.0.0 安装完成！
echo ========================================
echo.
echo   Token 保存至：
echo     %SYNCDIR%\.cloudpan\token.txt
echo.
echo   请打开此文件获取家庭共享 Token（配置客户端用）
echo   安全提示：配置完客户端后请删除此文件。
echo.
echo   管理命令：
echo     启动: sc start %SERVICE_NAME%
echo     停止: sc stop %SERVICE_NAME%
echo     状态: sc query %SERVICE_NAME%
echo     卸载: %~nx0 uninstall
echo.
pause
