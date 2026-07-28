@echo off
cd /d "%~dp0"

set "SYNCDIR=%~dp0CloudPan_Client"
if not exist "%SYNCDIR%" mkdir "%SYNCDIR%"

echo ========================================
echo   CloudPan Client v0.1.0
echo   ^(^) : %SYNCDIR%
echo   ^(^) : http://localhost:8443
echo   ^(^) : put file in above folder to sync
echo ========================================
echo.

dotnet run --project CloudPan.Client -- http://localhost:8443 "%SYNCDIR%"

pause
