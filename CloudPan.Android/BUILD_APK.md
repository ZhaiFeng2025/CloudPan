# CloudPan Android APK 构建说明

## 方法一：Android Studio（推荐）

1. 下载安装 [Android Studio](https://developer.android.com/studio)
2. 打开 `CloudPan.Android` 目录
3. 等待 Gradle 同步完成
4. 菜单：Build → Build Bundle(s) / APK(s) → Build APK(s)
5. APK 在 `app/build/outputs/apk/release/app-release.apk`

## 方法二：命令行

确保安装了 Java 17+ 和 Android SDK，设置 ANDROID_HOME：

```bash
# Windows
set ANDROID_HOME=%LOCALAPPDATA%\Android\Sdk
gradlew.bat assembleRelease

# 输出: app/build/outputs/apk/release/app-release.apk
```

## 签名说明

当前使用 debug keystore 签名（仅开发测试用）。
正式发布前应生成正式签名密钥并更新 `app/build.gradle.kts` 中的 signingConfigs。

## 安装到手机

1. 将 APK 复制到手机
2. 设置 → 安全 → 允许安装未知来源应用
3. 点击 APK 安装
4. 打开 App → 填写服务端地址 + Token → 连接
