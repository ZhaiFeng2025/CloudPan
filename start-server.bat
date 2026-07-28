@echo off
cd /d "%~dp0"

set "SYNCDIR=%~dp0CloudPanSync"
if not exist "%SYNCDIR%" mkdir "%SYNCDIR%"

echo ========================================
echo   CloudPan Server v0.1.0
echo   ^(^) : %SYNCDIR%
echo   ^(^) : http://localhost:8443
echo ========================================
echo.

dotnet run --project CloudPan.Server -- --SyncRoot "%SYNCDIR%"

pause
