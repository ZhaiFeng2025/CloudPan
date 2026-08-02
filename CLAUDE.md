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
- 数据库 SQLite WAL 模式，EF Core Code-First（**迁移至 Migrations 中**，见规则 8；迁移落地前 EnsureCreated + 建表兼容层必须保持可用）
- 文件存储为镜像目录结构（与同步根一致，原始文件可直接访问）
- 隐藏元数据目录 `.cloudpan`（DB、版本历史、缩略图）
- 原子写入：先写 `.tmp` → 校验 → rename
- 端口与配置常量定义在 `ContractManifest.g.cs` → `SpecPorts` / `SpecConfig`

### 3. 解决方案结构

**目标四层架构**（依赖方向单向：Host/UI → Core → Infrastructure → Contract）。存量代码在旧单块项目（`CloudPan.Server`/`CloudPan.Client`）中按架构重构计划迁移，**新代码必须按目标架构落位**（见规则 8）。

```
CloudPan.sln
├── CloudPan.Contract/        # 契约层：spec 生成物（DTO/枚举/端点/错误体）+ 传输协议抽象，零 UI 依赖
├── CloudPan.Infrastructure/  # 基础设施层：持久化(EF+Migrations)/文件存储/路径安全/密钥/配置/日志/重试（两端共用）
├── CloudPan.Server.Core/     # 服务端领域层：索引/版本/分享/回收站/分块上传/WebSocket 处理
├── CloudPan.Server.Host/     # 服务端宿主：HTTP 适配 + 中间件 + IHostedService 定时任务 + 薄组合根（Program.cs < 150 行）
├── CloudPan.Server.UI/       # 服务端管理 UI（托盘/窗口/安装器），可选，Host 不引用时可 headless
├── CloudPan.Client.Core/     # 客户端领域层：同步状态机/队列/游标/冲突/传输客户端
├── CloudPan.Client.UI/       # 客户端 WinForms 壳（只渲染）
├── CloudPan.CodeGen/         # 契约代码生成器（读 shared-spec.json → 生成 .g.cs）
├── CloudPan.Analyzers/       # Roslyn 自定义分析器（CP001-CP304，契约合规校验）
├── CloudPan.Tests/           # 单元测试 + 集成测试 + 架构测试 + 基准测试
└── CloudPan.Android/         # Kotlin Android 客户端（v1.0 为基础框架原型）
```

> 迁移说明：`CloudPan.Shared` 将演进为 `CloudPan.Contract`（移除 UI 依赖）；`CloudPan.Server` 拆为 Core/Host/UI 三个项目；`CloudPan.Client` 拆为 Core/UI 两个项目。完整定义见 [docs/architecture-requirements.md](docs/architecture-requirements.md) 与 [.claude/architecture-refactor-plan.md](.claude/architecture-refactor-plan.md)。

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

### 7. AI 协作约束（防系统性缺陷）

本项目大量代码由 AI 生成。AI 在以下维度存在**系统性盲区**（已在 v1.0.0 审查中验证），所有代码变更必须通过对应的检查点。

#### 7.1 跨模块依赖——"局部正确、全局错误"

AI 逐文件、逐方法生成代码时，每个独立片段正确，但片段之间的数据流依赖关系不被建模。

**强制规则：**
- 中间件/过滤器/Handler 的注册顺序必须验证数据流依赖。例如：若 M1 读取 `context.Items["X"]`，M2 写入该值，则 M2 必须在 M1 之前注册（`app.UseM2()` 先于 `app.UseM1()`）。
- 依赖注入的生命周期必须匹配：Singleton 不能依赖 Scoped；Timer 回调中不得访问 Scoped/Transient 服务。
- 事务边界必须覆盖所有会产生副作用的操作（DB 写入 + 文件系统操作）；任何非原子的 DB+FS 组合必须有一致性恢复路径。

#### 7.2 异步生命周期——"fire-and-forget 不是解决方案"

AI 常使用 `_ = SomeAsync()` 模式处理 Timer 回调或事件处理器中的异步操作，导致异常静默丢失。

**强制规则：**
- **禁止**在 `System.Threading.Timer` / `System.Timers.Timer` 回调中使用 `_ = SomeAsync()` 模式。
- Timer 回调如需异步操作，使用 `Task.Run(async () => { try { await ... } catch (Exception ex) { Log; } })` 并捕获全部异常。
- 所有 `CancellationTokenSource` 必须在不再使用时 `Dispose()`（通常在 `finally` 块或 `ApplicationStopped` 中）。
- `async void` 仅允许在 UI 事件处理器中使用，且**必须**有顶层 try-catch 覆盖整个方法体。

#### 7.3 异常恢复路径——"catch 块里的代码也需要验证"

AI 在主路径上推理强，在异常恢复路径上推理弱。catch 块中的逻辑常假设一个已经被异常破坏的状态仍然干净。

**强制规则：**
- catch 块中重用 `DbContext` 前，必须先验证变更追踪器状态（已跟踪的 `Added` 实体会导致 `FindAsync` 返回失败实体而非数据库真值 → 使用全新 `DbContext` 重试）。
- `AggregateException` 必须递归解包所有 `InnerExceptions`，不能只处理第一个。
- 事务回滚后，必须清理对应的**文件系统副作用**（已写入的临时文件、已移动的目录等）。
- 捕获 `DbUpdateException` 时必须区分"并发冲突（可重试）"和"约束违反（不可重试）"。

#### 7.4 并发安全——"看起来线程安全的代码通常不是"

AI 匹配标准代码模式时，不理解并发访问的生命周期约束。

**强制规则：**
- 任何被多个线程读写的字段必须有显式同步机制（`lock` / `Interlocked` / `volatile` / `ConcurrentDictionary`）。
- `long` 类型字段在可能运行于 32-bit 运行时时，读写必须使用 `Interlocked.Read()` / `Interlocked.Exchange()`。
- `WebSocket` / `HttpClient` / `DbContext` 等非线程安全对象的字段引用，在并发访问时必须用 `lock` 保护。
- 事件处理器（`event Action?`）在多线程订阅/取消订阅时存在竞态——要么只在单线程操作，要么在 `Dispose` 中置 null 清理。

#### 7.5 检查清单（每个 PR / 功能完成后）

在提交前，用以下三个视角**独立**审查变更（可分别用 Agent 执行）：

| 审查 Agent | 焦点 | 关键词检测 |
|---|---|---|
| **"顺序与依赖"** | 中间件注册顺序、DI 生命周期、事务边界、文件系统+DB 一致性 | `UseXxx()`, `AddSingleton`/`AddScoped`, `SaveChangesAsync` + `File.`, `BeginTransaction` |
| **"并发与生命周期"** | Timer 回调、async void、fire-and-forget、IDisposable 不配对、字段线程安全 | `Timer`, `async void`, `_ = `, `Dispose`, `volatile`/`lock`/`Interlocked` 缺失 |
| **"异常与恢复"** | catch 块正确性、AggregateException 解包、回滚逻辑、资源清理 | `catch`, `AggregateException`, `Rollback`, `try { File.Delete` |

#### 7.6 已知反模式（编译时零警告但运行时必炸）

以下模式 AI 频繁生成，Roslyn Analyzer 无法检测，必须人工/Agent 扫描：

```
1. app.UseRateLimit(); app.UseTokenAuth();
   → RateLimit 永远读不到 TokenAuth 设置的 DeviceId

2. Timer 回调: _ = UpdateDbAsync();
   → DB 写入失败静默丢失，设备状态僵尸化

3. catch (DbUpdateException) { var x = await db.FindAsync(id); db.Save(); }
   → FindAsync 返回变更追踪器中 Add 失败的实体，Save 二次冲突

4. 存档旧版本 → 分配新版本 → 写文件（三步独立，无事务）
   → 写文件失败，孤儿版本记录；存档成功写文件失败，DB 与 FS 不一致

5. string safeArg = EscapeCmdArg(userInput);
   Process.Start("cmd.exe", "/c sc create ... " + safeArg);
   → 仅转义双引号，& | < > ^ 未被转义，命令注入
```

### 8. 架构分层规则（强制，目标四层架构）

**任何代码变更必须遵守本节。** 完整规格见 [docs/architecture-requirements.md](docs/architecture-requirements.md)，迁移路径见 [.claude/architecture-refactor-plan.md](.claude/architecture-refactor-plan.md)。

```
宿主层（可变、可替换）          Server.Host / Server.UI / Client.UI
      ↓
领域层（独立于宿主，可单测）     Server.Core / Client.Core
      ↓
基础设施层（两端共用）          Infrastructure
      ↓
契约层（唯一事实来源，零 UI）   Contract
```

**强制规则：**

1. **依赖方向严格单向**：Host/UI → Core → Infrastructure → Contract。禁止反向引用、禁止跳层引用。
2. **分层禁引**：`Server.Core` 不得引用 `Microsoft.AspNetCore.*`；`Client.Core` 不得引用 `System.Windows.Forms`；Contract/Infrastructure/Core 均不得引用 UI。
3. **领域逻辑进 Core**：索引/版本/分享/回收站/同步状态机等必须位于 Core。Controller 只做 HTTP 适配（参数绑定/状态码/错误体），不得直接操作 DbContext/File。
4. **基础设施单一实现**：持久化/文件存储/路径安全/密钥/日志/配置/重试等两端共性设施必须位于 Infrastructure，禁止在 Server/Client 各自重复实现。
5. **路径安全统一防线**：所有"路径 → 绝对路径"转换必须经 `Infrastructure/Storage` 的 `ValidatePath` 校验，禁止在 Controller/Service 中自行拼接路径。
6. **定时任务用 IHostedService**：禁止裸 `System.Threading.Timer` 散落于 Program.cs。
7. **传输协议抽象在 Contract**：业务逻辑不得直接依赖具体 HTTP/WebSocket 类型，必须经 Contract 的协议抽象。
8. **单类行数 ≤ 400**：Core 拆分后超标必须拆分。

**迁移过渡期（当前）**：
- 存量代码在旧单块项目（`CloudPan.Server`/`CloudPan.Client`）中，按重构计划逐步迁移
- **新代码必须按目标架构落位**：新增领域逻辑进 Core，新增基础设施进 Infrastructure，禁止在旧项目新增领域层代码
- 依赖方向由架构测试守护（`CloudPan.Tests/Architecture/`）与 Analyzer 规则强制，违反即 CI 失败
- 业务逻辑与基础设施同时变动时，优先落位新架构再改逻辑，避免"先改逻辑后迁移"的双重风险
