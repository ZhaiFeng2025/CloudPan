@echo off
chcp 65001 >nul
set PROJDIR=%~dp0CloudPan.Android
set JAVA_HOME=%PROJDIR%\jdk
set PATH=%JAVA_HOME%\bin;%PATH%
echo JAVA_HOME=%JAVA_HOME%
echo Project: %PROJDIR%
echo Building APK...
echo.
call "%PROJDIR%\gradlew.bat" -p "%PROJDIR%" assembleDebug
echo.
if %errorlevel% equ 0 (
    echo ========================================
    echo BUILD SUCCESS
    echo APK: app\build\outputs\apk\debug\app-debug.apk
    echo ========================================
)
pause
