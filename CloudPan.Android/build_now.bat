@echo off
chcp 65001 >nul
title CloudPan APK Builder
echo.
echo   ========================================
echo     CloudPan Android APK Builder
echo   ========================================
echo.

REM 自动检测 JAVA_HOME
if "%JAVA_HOME%"=="" (
    if exist "%~dp0jdk\bin\java.exe" (
        set JAVA_HOME=%~dp0jdk
        echo   [OK] Found bundled JDK
    ) else if exist "%JAVA_HOME%\bin\java.exe" (
        echo   [OK] Using system JAVA_HOME
    ) else (
        echo   [ERROR] JAVA_HOME not set and no bundled JDK found.
        echo   Download JDK 17 from https://adoptium.net/ and set JAVA_HOME
        pause
        exit /b 1
    )
)
set PATH=%JAVA_HOME%\bin;%PATH%

REM 检查 wrapper jar
if not exist "%~dp0gradle\wrapper\gradle-wrapper.jar" (
    echo   [ERROR] gradle-wrapper.jar missing.
    echo   Run: gradle wrapper --gradle-version 8.4
    pause
    exit /b 1
)

echo   JAVA_HOME=%JAVA_HOME%
echo   Building APK...
echo.

call "%~dp0gradlew.bat" assembleDebug

if %errorlevel% equ 0 (
    echo.
    echo   ========================================
    echo   [SUCCESS] APK built!
    echo   Output: %~dp0app\build\outputs\apk\debug\app-debug.apk
    echo   ========================================
) else (
    echo.
    echo   [FAIL] Build failed.
    echo   Common fixes:
    echo   1. Install Android SDK: set ANDROID_HOME
    echo   2. Check network: gradlew downloads dependencies
    echo   3. Open in Android Studio for guided setup
)
pause
