# 架构审查知识库（CloudPan）

> 供任务产生者 task-producer 及其架构审查子 Agent 使用。审查以「目标四层架构 + 契约驱动」为准绳，参照整洁架构（Clean Architecture）与 SOLID。
> **目标服务**：可被 `goals.json` 的 `goal.kbRef` 引用为 assess 目标判据来源（文件 + §章节）。

## 1. 目标架构（项目绑定约束，违反即 P0）

- 四层单向依赖：`Host/UI → Core → Infrastructure → Contract`，禁止反向、禁止跳层
- 分层禁引：`Server.Core` 不得引用 `Microsoft.AspNetCore.*`；`Client.Core` 不得引用 `System.Windows.Forms`；Contract/Infrastructure/Core 不得引用 UI
- 领域逻辑进 Core：索引/版本/分享/回收站/同步状态机必须在 Core；Controller 只做 HTTP 适配（参数绑定/状态码/错误体），**不得直接操作 DbContext/File/拼路径**
- 基础设施单一实现：持久化/文件存储/路径安全/密钥/日志/配置/重试等两端共性设施必须在 Infrastructure，禁止两端各自重复实现
- 路径安全统一防线：所有"路径→绝对路径"转换必须经 `Infrastructure/Storage` 的 `ValidatePath`，禁止在 Controller/Service 自行拼接
- 定时任务用 `IHostedService`，禁止裸 `System.Threading.Timer` 散落 Program.cs
- 单类行数 ≤ 400，超标必须拆分

## 2. 契约驱动（项目绑定约束，违反即 P0）

- `shared-spec.json` 是唯一事实来源；枚举/DTO/实体/路由骨架/HttpClient 接口/错误码必须**从契约生成**，禁止手工翻译
- 生成物在 `Generated/`，文件名 `.g.cs`，头部标注 `AUTO-GENERATED from shared-spec.json`
- 校验：`dotnet run --project CloudPan.CodeGen -- --verify` 必须一致
- 业务逻辑只引用 Generated 类型，不自行重复定义

## 3. 依赖注入与数据流（审查重点）

- 中间件注册顺序：写入 `context.Items["X"]` 的必须先于读取的注册（数据流依赖）
- DI 生命周期：Singleton 不得依赖 Scoped；Timer/后台服务不得直接访问 Scoped（必须 `CreateScope`）
- 反模式：captive dependency、Controller 里 `new` 服务、静态 ServiceLocator、异步 void 泄漏

## 4. 行业参照（判据）

- **整洁架构**：依赖规则向内，业务核心不依赖框架细节
- **SOLID**：重点依赖倒置（DIP）、接口隔离（ISP）
- **六边形/洋葱**：适配器在边界，端口/领域在核心
- **契约即代码**：单一事实来源，改契约一处生效全部

## 5. 审查问题清单

- [ ] 新增代码落在哪一层？依赖方向是否向下单向？
- [ ] Controller 是否只做 HTTP 适配？有无直接碰 DbContext/File/路径拼接？
- [ ] 有无跳层引用 / 反向引用 / 两端重复实现？
- [ ] 契约变更是否走 `shared-spec.json` → 生成？有无手工翻译？
- [ ] 有无超 400 行的类、散落的裸 Timer？
- [ ] DI 注册顺序与生命周期是否符合数据流？
- [ ] 有没有为一次性用途建抽象层？
