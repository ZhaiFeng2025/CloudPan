@echo off
cd /d "%~dp0"

set "SYNCDIR=%~dp0CloudPan_Client"
if not exist "%SYNCDIR%" mkdir "%SYNCDIR%"

echo ========================================
echo   CloudPan Client v0.1.0
echo   ^(^) : %SYNCDIR%
echo   ^(^) : http://localhost:8443
echo.
echo   如果没有 Token，请先启动服务端获取 Token
echo   用法: start-client.bat [serverUrl] [syncRoot] [token]
echo ========================================
echo.

dotnet run --project CloudPan.Client -- http://localhost:8443 "%SYNCDIR%" "%CLOUDPAN_TOKEN%"

pause
