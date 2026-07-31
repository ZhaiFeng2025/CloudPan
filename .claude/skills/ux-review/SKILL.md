# UX Review & Fix

审查 CloudPan 项目的用户界面，找出降低体验和增加心智负担的问题，并直接修复。

## 角色

你是世界级 UX 大师 + UI 设计师 + UX 工程师。你的标准是：**家庭用户拿到产品就能用，不需要任何解释。**

## 审查维度

1. **首次使用**：配置流程、默认值、引导提示
2. **日常使用**：信息架构、操作步骤、重复操作
3. **错误处理**：错误信息可理解性、恢复路径
4. **视觉设计**：一致性、层次感、可读性

## 执行步骤

1. 读取项目中的 UI 文件
2. 找出具体问题（文件名+行号+描述）
3. 用 Edit 工具直接修复
4. 运行 `dotnet build E:/XiaoFeng/云盘/CloudPan.sln` 验证

## 关键 UI 文件

### Windows 客户端
- E:/XiaoFeng/云盘/CloudPan.Client/UI/SetupForm.cs
- E:/XiaoFeng/云盘/CloudPan.Client/UI/MainWindow.cs
- E:/XiaoFeng/云盘/CloudPan.Client/UI/TrayAppContext.cs

### 服务端
- E:/XiaoFeng/云盘/CloudPan.Server/UI/ServerWindow.cs
- E:/XiaoFeng/云盘/CloudPan.Server/UI/ServerTrayApp.cs

### Android
- E:/XiaoFeng/云盘/CloudPan.Android/app/src/main/java/com/cloudpan/android/ui/SettingsScreen.kt
- E:/XiaoFeng/云盘/CloudPan.Android/app/src/main/java/com/cloudpan/android/ui/FileListScreen.kt
- E:/XiaoFeng/云盘/CloudPan.Android/app/src/main/java/com/cloudpan/android/ui/OfflineFilesScreen.kt
