# CloudPan 架构重构计划（四层拆分）

> **版本**: v1.1（执行完成）
> **日期**: 2026-08-02
> **决策**: 四层完整拆分（Contract / Core / Infrastructure / Host+UI）；两端（Server/Client）并行迁移
> **原则**: 增量重构不停车——每个阶段结束时可编译、测试全绿、可发布。不推倒重来。
>
> **执行状态**: ✅ 已完成（2026-08-02）
> - S0 契约纯化（CloudPan.Contract）+ 基础设施层（CloudPan.Infrastructure）+ 架构测试门禁 ✅
> - S1 服务端拆分为 Core/Host/UI 三项目 ✅；客户端拆分为 Core/UI 两项目 ✅
> - SyncEngine 1441 行 → 5 个 partial 分片（最大 415 行）✅；Program.cs 686→137 行 ✅
> - WS 认证缺陷、SettingsStore 陈旧路径缺陷在重构中修复 ✅
> - 验收：Release 编译 0 error、95 测试全绿、架构测试零违反、契约 --verify 通过、headless 运行验证通过 ✅

---

## 一、为什么要拆（三个结构性病灶）

1. **领域逻辑与宿主耦合在单项目**：`FilesController.cs` 921 行直接操作 DbContext+FS；`SyncEngine.cs` 1441 行承担状态机/队列/传输/冲突/进度。领域逻辑无法脱离 ASP.NET/WinForms 测试——不是测试写得少，是架构让测试写不出来。
2. **基础设施散落两端各写一套**：服务端 `SecretStore`（明文文件）vs 客户端 `SettingsStore`（DPAPI）；两套 DbContext；`ValidatePath` 只在 FileStorageService 一处 → 缩略图穿越漏洞漏网。
3. **组合根膨胀**：Server `Program.cs` 686 行（中间件/建表/种子/4 个 Timer/UDP/三形态切换）；Client `Program.cs` 469 行。任何启动行为改动都要碰它们。

---

## 二、目标架构（四层依赖金字塔）

```
宿主层（可变、可替换）
  CloudPan.Server.Host     HTTP 适配 + 后台任务 + 薄组合根（Program.cs < 150 行）
  CloudPan.Server.UI       （可选）托盘 / 窗口 / 安装器
  CloudPan.Client.UI       WinForms 壳，只做渲染
        │
领域/应用层（独立于宿主，可单测）
  CloudPan.Server.Core     索引 / 版本 / 分享 / 回收站 / 分块上传 —— 领域服务
  CloudPan.Client.Core     同步状态机 / 队列 / 游标 / 冲突 —— 领域服务
        │
基础设施层（两端共用，独立于领域）
  CloudPan.Infrastructure  持久化(EF+迁移) / 文件存储(原子写+ValidatePath)
                          / 安全(密钥·哈希·指纹) / 配置 / 日志 / 健康 / 重试
        │
契约层（唯一事实来源，纯协议，零 UI 依赖）
  CloudPan.Contract        spec 生成的 DTO/枚举/端点/错误体 + 传输协议抽象
```

**依赖规则（强制，架构测试守护）**：
- Host/UI → Core → Infrastructure → Contract，严格单向，禁止反向或跳层
- `Server.Core` **不引用** ASP.NET；`Client.Core` **不引用** WinForms
- `Contract` 移除 `UI/ServerIcons.cs`（挪回各 UI 项目）、移除 `System.Drawing.Common`
- `Infrastructure` 独立于领域：不引用 Core

---

## 三、模块划分清单

### 3.1 CloudPan.Contract（契约层）

| 职责 | 关键类型 | 迁移动作 |
|---|---|---|
| spec 生成物 | `Generated/`：Enums/Dtos/ContractManifest/ErrorResponse/ApiResponses `.g.cs` | 保留 |
| 传输协议抽象 | `ISyncTransport`、分页游标、错误体类型 | 新增（从 spec `api.responses`/`websocket` 推导） |
| UI 依赖 | `UI/ServerIcons.cs`、`System.Drawing.Common` | **移除** → 挪到 Server.UI / Client.UI |

### 3.2 CloudPan.Infrastructure（基础设施层，两端共用）

| 子域 | 关键类型 | 解决什么 |
|---|---|---|
| `Persistence/` | 统一 DbContext、**EF Core Migrations** 替换 `EnsureCreated`+建表兼容层 | 数据库可升级、两端统一 |
| `Storage/` | FileStorageService：原子写入、`ValidatePath` 统一防线、`GetAbsolutePath` 边界 | 路径穿越从根上堵住 |
| `Security/` | 统一密钥存储（取代明文 SecretStore）、哈希、证书指纹 | 两端安全原语一套实现 |
| `Configuration/` | 配置加载/合并（Server appsettings+AppConfig；Client JSON） | 消除两端各自配置代码 |
| `Diagnostics/` | Serilog 初始化、健康检查、内存监控 | 日志初始化一处完成 |
| `Http/` | 重试/退避策略 | ApiClient 与 WebSocketClient 共用 |

### 3.3 CloudPan.Server.Core（领域层）

| 子域 | 关键类型 | 迁移动作 |
|---|---|---|
| `FileIndex/` | `IFileIndexService`+实现 | 从 Server 抽出 |
| `Versioning/` | `IVersionService`、**Restore 事务化** | 抽出 + 修复非原子 |
| `Sharing/` | ShareService：**原子计数、慢哈希、按 IP 限流** | 抽出 + 修复 |
| `Trash/` | TrashService（含 30 天清理） | 从 Server 抽出 |
| `ChunkedUpload/` | 分块上传状态机 | 抽出 |
| `WebSocket/` | `IWebSocketHandler` + 连接处理（领域逻辑） | 抽出 |
| `Ports/` | 领域所需外部接口（存储/密钥/时间等） | 新增 |

### 3.4 CloudPan.Server.Host（宿主层）

- `Controllers/` 8 个：只做 HTTP 适配（参数绑定/状态码/错误体），调 Core
- `Middleware/` 4 个：HTTP 关注点，留在 Host
- `Background/`：回收站清理、WAL checkpoint、内存监控、chunk 清理 → 改为 `IHostedService`
- `UdpDiscovery/`：UDP 发现
- `Program.cs`：薄组合根（<150 行）

### 3.5 CloudPan.Server.UI（可选）

- `ServerTrayApp` / `ServerWindow` / `ServerInstaller` / 图标。Host 不引用 UI 时 headless 可跑。

### 3.6 CloudPan.Client.Core（领域层）

| 子域 | 关键类型 | 迁移动作 |
|---|---|---|
| `Sync/` | SyncEngine **拆分为**：状态机 / 队列管理器 / 传输调度 / 冲突处理 / 监控 | 从 Client 抽出 + 修复缺陷 |
| `Queue/` | SyncQueue 持久化队列 | 抽出 |
| `Snapshot/` | RemoteSnapshot、SyncCursor | 抽出 |
| `Transport/` | ApiClient、WebSocketClient（客户端专属基础设施） | 抽出，改依赖 Contract 协议抽象 |

### 3.7 CloudPan.Client.UI（WinForms 壳）

- `MainWindow` / `SetupForm` / `TrayAppContext` / `SelectiveSyncPanel` / `SettingsForm`，只做渲染，不再含业务逻辑。

---

## 四、迁移阶段（两端并行，每阶段可发布）

### S0 — 前置：契约纯化 + 基础设施骨架 + 架构测试就位
- [ ] 建 Contract/Core/Infrastructure 项目骨架；`Contract` 移除 UI 依赖
- [ ] Infrastructure 落地：统一 DbContext + 首版 EF Migrations、Storage（ValidatePath 统一防线）、Security（统一密钥存储）、Diagnostics（Serilog 一处初始化）
- [ ] 架构测试硬门禁就位（见 §六）；CI 更新为新项目矩阵
- **验收**：全解决方案可编译，测试全绿，`--verify` 通过

### S1 — 并行迁移
**工作流 A（服务端）**：
- [ ] 领域逻辑搬入 Server.Core（FileIndex→Versioning→Sharing→Trash→ChunkedUpload→WebSocket 逐块迁移）
- [ ] Controller 变薄为 HTTP 适配层；中间件留 Host；定时任务改 IHostedService
- [ ] Program.cs 瘦身至 <150 行；Host 与 UI 项目分离，验证 headless 启动
- [ ] 修复：WS 认证链路（跨 TokenAuthMiddleware↔WebSocketHandler↔WebSocketClient 的数据流显式化）

**工作流 B（客户端）**：
- [ ] SyncEngine 拆分（状态机/队列/传输/监控）；ApiClient/FileWatcher/WS 归入 Client.Core
- [ ] UI 窗口瘦身（MainWindow 1746 行 → 只渲染）
- [ ] 修复：双全量扫描合并、下载失败收敛、目录同步接通

**验收（两端）**：领域逻辑新增单元测试覆盖；`dotnet build -c Release /warnaserror` 通过；同步冒烟（test.ps1）通过

### S2 — 验证与收尾
- [ ] 覆盖率接入 CI + 门禁（对齐 release-verification-plan 65%）
- [ ] 发布流程走 release-verification-plan 全流程；版本 bump spec v1.1.0 → 重跑 CodeGen
- [ ] 架构测试全绿；行数门禁逐类核查（Core 拆后无 God Object）

---

## 五、明确不做（边界与取舍）

| 不做 | 理由 |
|---|---|
| 微服务化 / 独立 MQ / 独立缓存 / APM | 家庭单进程，`IMemoryCache` + SQLite 队列 + 日志足够 |
| 大爆炸重写 | v1.0 功能已可用，增量重构不停车 |
| Server/Client 拆成多进程（同步服务 vs 控制面板） | 家庭部署复杂度上升，收益不成比例；用"UI 可选引用"达到同样灵活性 |
| 本轮不做 Android/Web 迁移 | 先完成 C# 侧分层，契约全平台（S4）在分层稳定后进行 |

---

## 六、守护机制（防架构退化）

1. **架构测试**：扩展现有 `CodeQualityTests` 为硬门禁——每个项目引用集 ⊆ 下层项目集；CI 强制（编译期 Assert，而非 PowerShell 扫描）
2. **Analyzer 扩展**：新增规则——Controller 不得直接访问 `DbContext`/`File`（对应现有 15 个分析器风格）；`Server.Core` 不得引用 `Microsoft.AspNetCore` 命名空间
3. **行数门禁**：Core 拆后逐类设上限（对标 release-verification-plan Stage 5.4）

---

## 七、验收标准（架构重构完成定义）

- [ ] 依赖方向 100% 单向，架构测试零违反
- [ ] Server `Program.cs` < 150 行；FilesController < 400 行；SyncEngine 拆为 <400 行/模块
- [ ] headless 启动验证：Host 不引用 UI 时 Kestrel 正常服务 + 同步可用
- [ ] 两端基础设施无重复实现（密钥/日志/路径安全/重试 各一套）
- [ ] 领域逻辑单测覆盖：WS 握手、版本事务、回收站、同步失败收敛（重构前无法测，重构后可测）
- [ ] `--verify`、架构测试、覆盖率门禁、Release 发布全流程通过
