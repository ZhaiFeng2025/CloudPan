# CloudPan 架构需求规格

> **版本**: v1.0
> **状态**: 已批准（2026-08-02，经架构审查确认）
> **作者**: 架构设计 + 用户评审
> **定位**: 目标架构的正式需求规格（"系统应该是什么样"）。迁移路径见 `.claude/architecture-refactor-plan.md`，两者互补，冲突时以本文档为准。

---

## 1. 背景与目标

### 1.1 背景

CloudPan 是自托管家庭文件同步系统，v1.0 功能完整可发布。但当前单块架构存在三个结构性病灶：

1. **领域逻辑与宿主耦合在单项目**——`FilesController.cs` 921 行直接操作 DbContext+FS，`SyncEngine.cs` 1441 行承担状态机/队列/传输/冲突/进度，领域逻辑无法脱离 ASP.NET/WinForms 测试。
2. **基础设施散落两端各写一套**——服务端明文 `SecretStore` vs 客户端 DPAPI；两套 DbContext；路径安全校验无统一防线（已发生缩略图路径穿越漏洞）。
3. **组合根膨胀**——Server `Program.cs` 686 行、Client `Program.cs` 469 行，启动行为改动必须触碰巨型组合根。

### 1.2 目标

将 CloudPan 重构为**四层依赖架构**，达成以下质量目标：

| 目标 | 需求编号 |
|---|---|
| 领域逻辑可脱离 HTTP/UI 独立单元测试 | R-Q1 |
| 两端基础设施单一实现，消除复制 | R-Q2 |
| 服务端可 headless 部署（Host 不依赖 UI） | R-Q3 |
| 架构约束可被机器守护（编译期/CI） | R-Q4 |
| 增量重构不停车，每阶段可发布 | R-Q5 |

---

## 2. 需求范围

本文档覆盖 **C# 服务端与客户端**的目标架构。Android（Kotlin）与 Web 客户端在架构稳定后进行契约对接（见 R-A6），不在本文档的模块拆分范围内。

---

## 3. 架构需求规格

### 3.1 分层架构模型

系统分为四层，依赖方向**严格单向**：

```
宿主层（可变、可替换）
  Server.Host / Server.UI / Client.UI
        │ 允许 ↓，禁止 ↑
领域层（独立于宿主，可单测）
  Server.Core / Client.Core
        │ 允许 ↓，禁止 ↑
基础设施层（两端共用，独立于领域）
  Infrastructure
        │ 允许 ↓，禁止 ↑
契约层（唯一事实来源，纯协议，零 UI 依赖）
  Contract
```

### 3.2 各层职责与模块清单

#### 3.2.1 契约层 `CloudPan.Contract`

| 模块 | 职责 |
|---|---|
| `Generated/` | spec 生成物：DTO、枚举、端点表、错误码、协议类型（唯一事实来源，禁止手工翻译） |
| `Protocol/` | 传输协议抽象：`ISyncTransport`、分页游标、错误体类型（从 spec `api.responses`/`websocket` 推导） |
| — | **禁止**：引用 UI/图形库；依赖 `System.Drawing.Common` |

#### 3.2.2 基础设施层 `CloudPan.Infrastructure`

| 子域 | 职责 |
|---|---|
| `Persistence/` | 统一 DbContext、**EF Core Migrations**（替换 `EnsureCreated` + 手工建表兼容层） |
| `Storage/` | 文件存储：原子写入（`.tmp` → 校验 → rename）、`ValidatePath` 统一防线、`GetAbsolutePath` 边界 |
| `Security/` | 密钥存储（取代服务端明文 SecretStore）、哈希、证书指纹 |
| `Configuration/` | 配置加载与合并（Server appsettings+AppConfig；Client JSON） |
| `Diagnostics/` | 日志初始化（Serilog 一处完成）、健康检查、内存监控 |
| `Http/` | 重试/退避策略（ApiClient 与 WebSocketClient 共用） |

#### 3.2.3 领域层

**`CloudPan.Server.Core`**：索引（FileIndex）、版本历史（Versioning，Restore 必须事务化）、分享（Sharing，原子计数/慢哈希/限流）、回收站（Trash）、分块上传（ChunkedUpload）、WebSocket 连接处理（领域逻辑）。对外通过 `Ports/` 接口依赖基础设施。

**`CloudPan.Client.Core`**：同步状态机、队列管理器（SyncQueue 持久化）、游标（RemoteSnapshot/SyncCursor）、冲突处理、传输客户端（ApiClient/WebSocketClient 实现 Contract 的协议抽象）。

#### 3.2.4 宿主层

**`CloudPan.Server.Host`**：Controllers（只做 HTTP 适配：参数绑定/状态码/错误体，不写领域逻辑）、Middleware（HTTP 关注点）、`IHostedService` 定时任务（回收站清理/WAL checkpoint/内存监控/chunk 清理）、UDP 发现、薄组合根（Program.cs < 150 行）。

**`CloudPan.Server.UI`**（可选）：托盘、管理窗口、安装器。Host 不引用 UI 时可 headless 运行。

**`CloudPan.Client.UI`**：WinForms 壳，只做渲染，不承载业务逻辑。

### 3.3 依赖与约束规则（强制）

| 编号 | 规则 |
|---|---|
| R-A1 | 依赖方向严格单向：Host/UI → Core → Infrastructure → Contract。禁止反向引用、禁止跳层引用 |
| R-A2 | `Server.Core` 不得引用 `Microsoft.AspNetCore.*`；`Client.Core` 不得引用 `System.Windows.Forms`；Contract/Infrastructure/Core 均不得引用 UI |
| R-A3 | 领域逻辑必须位于 Core；Controller 只做 HTTP 适配 |
| R-A4 | 两端共用的基础设施必须位于 Infrastructure，禁止在 Server/Client 各自重复实现 |
| R-A5 | `ValidatePath` 为路径安全统一防线：所有"路径 → 绝对路径"转换必须经 `Infrastructure/Storage` 校验，禁止在 Controller/Service 中自行拼接路径 |
| R-A6 | 定时任务必须实现为 `IHostedService`，禁止裸 `System.Threading.Timer` 散落于 Program.cs |
| R-A7 | 传输协议抽象定义在 Contract；业务逻辑不得直接依赖具体 HTTP/WS 类型 |
| R-A8 | 契约层零 UI 依赖（不含 System.Drawing 等） |
| R-A9 | 单类行数上限 400（Core 拆分后），超标必须拆分 |

### 3.4 技术约束

| 项 | 约束 |
|---|---|
| 框架 | .NET 8（`net8.0`）；宿主/UI 项目为 `net8.0-windows` |
| 数据库 | SQLite WAL；**EF Core Migrations** 管理 schema（迁移落地前，新旧代码必须保持 `EnsureCreated` + 兼容层可用） |
| 文件存储 | 镜像目录结构；原子写入；隐藏元数据目录 `.cloudpan` |
| 契约 | `shared-spec.json` 唯一事实来源；代码生成器校验 CI 强制（见 CLAUDE.md 规则 0） |
| 端口/配置 | 常量定义在 `ContractManifest.g.cs` → `SpecPorts` / `SpecConfig` |

### 3.5 质量属性需求

| 编号 | 需求 | 验收方式 |
|---|---|---|
| R-Q1 | 领域逻辑可脱离 HTTP/UI 单元测试 | Core 项目无宿主依赖，`WebApplicationFactory` 仅用于 Host 集成测试 |
| R-Q2 | 两端基础设施单一实现 | 架构测试扫描无重复的密钥/日志/路径安全实现 |
| R-Q3 | Server 可 headless 启动 | Host 不引用 UI 时编译通过、Kestrel 正常服务、同步可用 |
| R-Q4 | 架构约束机器守护 | 架构测试（编译期 Assert）+ Analyzer 规则，CI 强制，违反即失败 |
| R-Q5 | 增量重构不停车 | 每个迁移阶段可编译、测试全绿、可发布 |
| R-Q6 | 领域逻辑可测缺陷闭环 | WS 握手、版本事务、回收站、同步失败收敛均有单测（重构前无法测） |

---

## 4. 关键架构决策记录（ADR）

| 决策 | 选择 | 理由 | 明确拒绝 |
|---|---|---|---|
| ADR-1 分层粒度 | 按领域边界分四层 | 家庭单进程，按技术层微服务化无收益；四层分离"不变的领域"与"会变的部署面" | 微服务化、独立 MQ/缓存/APM |
| ADR-2 基础设施独立成模块 | 独立为 `Infrastructure` 且两端共用 | 消除复制、统一安全防线（路径/密钥） | 不独立 → 重复实现持续累积 |
| ADR-3 服务端部署形态 | 单进程多形态（service/console/tray），Host 与 UI 项目分离 | 家庭部署简单；项目分离使 UI 成为可选，达到 headless 灵活性 | 拆成"同步服务进程 + 控制面板进程"（复杂度不成比例） |
| ADR-4 迁移方式 | 增量重构不停车 | v1.0 已发布，功能不可停摆 | 大爆炸重写 |
| ADR-5 schema 管理 | EF Core Migrations 替换 EnsureCreated | 数据库可升级（当前建表兼容层不可演进） | 长期保留 EnsureCreated + 手工兼容层 |
| ADR-6 Android/Web 对接 | 分层稳定后再做契约对接（S4） | 先完成 C# 侧分层，避免在迁移中同时改多平台 | 本轮同时迁移 Android |

---

## 5. 验收标准（架构重构完成定义）

- [ ] 依赖方向 100% 单向，架构测试零违反（R-A1~R-A9）
- [ ] Server `Program.cs` < 150 行；FilesController < 400 行；SyncEngine 拆为 <400 行/模块
- [ ] headless 启动验证通过（R-Q3）
- [ ] 两端基础设施无重复实现（R-Q2）
- [ ] 领域逻辑单测覆盖 WS 握手/版本事务/回收站/同步失败收敛（R-Q6）
- [ ] `--verify`、架构测试、覆盖率门禁、Release 发布全流程通过

---

## 6. 关联文档

| 文档 | 关系 |
|---|---|
| [.claude/architecture-refactor-plan.md](../.claude/architecture-refactor-plan.md) | 迁移路径（S0→S2 阶段，两端并行） |
| [.claude/release-verification-plan.md](../.claude/release-verification-plan.md) | 发布验收流水线（架构测试/覆盖率/安全为其中 Stage） |
| [CLAUDE.md](../CLAUDE.md) | 项目规则（架构分层规则为其中第 8 节，强制执行） |
| [shared-spec.json](../shared-spec.json) | 契约唯一事实来源 |
