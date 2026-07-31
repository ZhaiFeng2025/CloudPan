@echo off
chcp 65001 >nul
title CloudPan Android APK Builder

echo.
echo   ========================================
echo     CloudPan Android APK 构建
echo   ========================================
echo.

REM 检查 Java
java -version >nul 2>&1
if %errorlevel% neq 0 (
    echo   [ERROR] 未找到 Java。请安装 JDK 17:
    echo   https://adoptium.net/download/
    echo.
    pause
    exit /b 1
)
echo   [OK] Java 已安装

REM 检查 ANDROID_HOME
if "%ANDROID_HOME%"=="" (
    set ANDROID_HOME=%LOCALAPPDATA%\Android\Sdk
    echo   [INFO] ANDROID_HOME 未设置，使用默认: %ANDROID_HOME%
)

REM 检查 Android SDK
if not exist "%ANDROID_HOME%\platforms\android-34" (
    echo   [WARN] Android SDK 34 未找到，将自动下载
    echo   可能需要几分钟...
)

REM 构建
echo.
echo   [BUILD] 开始构建 APK...
echo.

call gradlew.bat assembleRelease

if %errorlevel% equ 0 (
    echo.
    echo   ========================================
    echo   [SUCCESS] APK 构建成功！
    echo.
    echo   输出位置:
    echo   app\build\outputs\apk\release\app-release.apk
    echo   ========================================
) else (
    echo.
    echo   [FAIL] 构建失败，请检查错误信息。
    echo   常见问题：
    echo   1. Android SDK 未安装 - 请安装 Android Studio
    echo   2. 网络问题 - 检查是否能访问 dl.google.com
)

echo.
pause
