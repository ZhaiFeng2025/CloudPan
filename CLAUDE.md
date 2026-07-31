# CloudPan 项目规则

## 项目概要

自托管家庭文件同步系统。C# / .NET 8 + WinForms（Windows）、Kotlin（Android）、SQLite + EF Core、ASP.NET Core 8 Kestrel。当前版本 **v1.0.0 正式发布版**。

## 核心规则

### 0. 契约驱动代码生成（最高优先级）

**`shared-spec.json` 是唯一事实来源。所有可从契约推导的代码必须从契约生成，禁止手工翻译。**

原因：手工翻译必然导致文档与代码不一致——这是 AI 长链路开发和人工维护共有的头号缺陷。契约即代码，改一处生效全部。

适用场景：
- **枚举定义**：C# `enum`、Kotlin `enum class` 的值必须与 `shared-spec.json → enums` 一致
- **DTO / Entity**：C# `record`、EF Core 实体、Kotlin `data class` 必须从 `shared-spec.json → entities` 生成，字段名与 `apiMapping` 对齐
- **API 路由骨架**：ASP.NET Controller 路由模板必须与 `shared-spec.json → api.endpoints` 一致
- **HttpClient 接口**：C# 客户端 `ApiClient`、Kotlin Retrofit `interface` 从 endpoints 生成
- **错误码**：`HttpErrorCode` 枚举从 spec 生成，包含 HTTP 状态码、code 字符串、retry 标记

代码生成器位于 `CloudPan.CodeGen/` 项目，形式为 C# Script（.csx）或独立控制台项目，脚本从 `shared-spec.json` 读取并输出到各项目的 `Generated/` 目录。

**核心约定：**
- 所有生成文件放在 `Generated/` 子目录，文件名以 `.g.cs` / `.g.kt` 结尾
- 生成文件头部标注 `// AUTO-GENERATED from shared-spec.json v{version} — DO NOT EDIT`
- `shared-spec.json` 版本号变更时，必须重跑代码生成器
- 业务逻辑代码**只引用** Generated 目录中的类型，不自行重复定义

**校验命令：**
```bash
# 生成所有代码
dotnet run --project CloudPan.CodeGen

# 校验模式：比较生成输出与当前文件是否一致（CI 用）
dotnet run --project CloudPan.CodeGen -- --verify
```

不做代码生成的例外（需在代码中注释说明理由）：
- 纯内部类型（不跨进程/不对外暴露）
- WinUI 3 XAML 绑定用的 ViewModel（可从 DTO 手工适配）

### 1. 版本与发布状态

当前 **v1.0.0 正式发布版**。核心功能完整：文件同步、版本历史、回收站、分块上传、分享链接、缩略图、冲突处理、速率限制、管理面板、UDP 局域网发现。

v1.0 技术范围：
- 家庭局域网内使用 HTTP（未启用 TLS）
- Windows 服务端 + 客户端（WinForms）
- Android 客户端原型（`CloudPan.Android/`，v1.0 为 Android 基础框架）
- 尚未启用自动更新（计划 v1.1）

### 2. 技术约束

- 所有项目目标 `net8.0-windows`（服务端和客户端均 Windows）
- 服务端监听端口由 `SpecPorts.HttpPort` 定义（当前 8443，HTTP，家庭局域网）
- 数据库 SQLite WAL 模式，EF Core Code-First（使用 EnsureCreated）
- 文件存储为镜像目录结构（与同步根一致，原始文件可直接访问）
- 隐藏元数据目录 `.cloudpan`（DB、版本历史、缩略图）
- 原子写入：先写 `.tmp` → 校验 → rename
- 端口与配置常量定义在 `ContractManifest.g.cs` → `SpecPorts` / `SpecConfig`

### 3. 解决方案结构

```
CloudPan.sln
├── CloudPan.Shared/          # 共享类型（从 spec 生成的枚举 + DTO + 契约清单 + 设计令牌）
├── CloudPan.Server/          # ASP.NET Core 服务端（Windows Service + 托盘 + 管理窗口）
├── CloudPan.Client/          # Windows Forms 桌面客户端（托盘常驻 + 管理窗口）
├── CloudPan.CodeGen/         # 契约代码生成器（读 shared-spec.json → 生成 .g.cs）
├── CloudPan.Analyzers/       # Roslyn 自定义分析器（CP001-CP304，契约合规校验）
├── CloudPan.Tests/           # 单元测试 + 集成测试 + 架构测试 + 基准测试
└── CloudPan.Android/         # Kotlin Android 客户端（v1.0 为基础框架原型）
```

### 4. 命名约定

- C# 命名遵循 .NET 规范（PascalCase 公有成员，camelCase 私有字段）
- API JSON 字段名 camelCase（与 `shared-spec.json → apiMapping` 一致）
- 数据库列名 PascalCase（EF Core 默认）
- 文件路径以 `/` 开头，目录以 `/` 结尾

### 5. 同步模型关键点

- 变更检测：FileSystemWatcher（主通道）+ 5 分钟定时全量扫描（兜底/正确性保证）
- 版本号：全局单调递增，服务端通过 AppConfig 原子分配
- 传输前：SHA-256 比对，哈希 + 大小均相同 → 跳过
- 下载后：SHA-256 校验，不匹配 → 重传
- 客户端状态机：空闲 → 扫描变更 → 比对哈希 → 传输 → 冲突检查 → 应用变更 → 空闲

### 6. 输出语言

中文优先。代码注释、提交信息、文档均使用中文。
