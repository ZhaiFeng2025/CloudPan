# 任务矩阵规范（契约 schema v4）

> 本规范是任务集的唯一事实来源，集群内所有 Agent（producer/executor/verifier）与 `/mission` 技能共同遵守。执行者与验收者开始操作契约前，必须阅读本规范。
> 本文件从 `task-producer.md` §4 拆分独立，**禁止在 producer/executor/verifier 定义中重复内嵌本规范**（双源必漂移）。

## 4.1 存储形态：结构化契约（目标 + 分片）+ 生成视图

```
docs/task-matrix/
├── contract/               # 契约：唯一事实来源（v4 目标 + 分片）
│   ├── meta.json
│   ├── state.json
│   ├── goals.json          # ★ 目标契约（目标 + 指标 + 基线 + 目标值）
│   ├── findings-index.json # findings 摘要（id/title/location，archive.py 生成，供去重/编号，不读全量 findings）
│   ├── active/T-{id}.json
│   ├── history/batch-{NN}.json
│   ├── findings.json       # 完整 findings（problem/why 追溯）
│   └── tasks-index.json
├── .reviews/               # 审查交接：{dim}.json 发现 + goals/{category}.json 度量
├── .run/                   # 运行时（gitignore）：wave-checkpoint.json 中断恢复 + goal-health.json 自动优化 + target-revisions.json 自动修订
├── INDEX.md                # 生成视图：活状态板 + 目标面板（archive.py 渲染）
└── batches/
    └── batch-01-2026-08-02.md   # 生成视图：第 N 批次完整任务文档（含问题清单）
```

**数据流**：
- 每次运行追加新批次 → 更新 `contract/` → 自检 → 渲染视图
- 状态只推进在 `contract/`；生成视图一律由脚本/契约渲染，**禁止手改视图**
- 聚合操作（done 卡归档、INDEX 渲染、state 同步、findings-index 重建）由 `python docs/task-matrix/tools/archive.py` 幂等执行；**目标度量合并**（`.reviews/goals/` → `goals.json`）由 `archive.py --goals` 幂等执行，AI 不手写

## 4.2 契约 schema（v4）

**单任务卡**（`active/T-{id}.json`，完整字段）：

```json
{
  "id": "T-001",
  "title": "把文件索引逻辑下沉 Server.Core",
  "dimension": "architecture",
  "priority": "P1",
  "status": "todo",
  "batch": 1,
  "findingId": "F-01",
  "goalRef": null,
  "dependsOn": [],
  "location": "CloudPan.Server/Controllers/FileController.cs:120",
  "scope": "FileController / Server.Core",
  "requirements": ["把索引逻辑从 Controller 移至 Server.Core 服务", "Controller 改为注入该服务"],
  "goal": "Controller 只做 HTTP 适配，不再直接触碰 DbContext",
  "acceptanceCriteria": [
    { "text": "架构测试全绿", "verification": "自动", "command": "dotnet test --filter Architecture" },
    { "text": "FileController 无直接 DbContext 引用", "verification": "自动", "command": "grep -n '_db\\|DbContext' CloudPan.Server.Host/Controllers/FileController.cs 无匹配" }
  ],
  "attempts": 0,
  "statusReason": null,
  "note": null,
  "updatedAt": null
}
```

**聚合文件**：
- `goals.json`：`{ "schemaVersion": 4, "goals": [ { id, category, level, parent, benchmark, kbRef, title, metric, measure, direction, baseline, target, currentValue, progress, status, achievedCondition, relatedTasks, createdBatch, lastMeasuredAt, measureNote } ] }`
- `state.json`：`{ "active": [ { id, title, dimension, priority, status, batch, dependsOn, attempts } ] }`（由 archive.py 从 active 卡同步，卡为真值）
- `tasks-index.json`：`{ "tasks": [ { id, title, dimension, priority, status, batch, location, goalRef } ] }`
- `history/batch-{NN}.json`：`{ "batch", "date", "conclusion", "dimensionSummary", "tasks": [完整卡...] }`
- `findings.json`：`[ { id, dimension, severity, location, problem, why } ]`（完整追溯）
- `findings-index.json`：`{ "findings": [ { id, dimension, severity, title, location } ] }`（摘要，archive.py 重建）

**任务字段表**（v3 一致，新增 `goalRef`）：

| 字段 | 类型 | 约束 |
|---|---|---|
| `schemaVersion` | int | 契约版本，当前 **4**（v2 的 tasks.json 保留为归档） |
| `id` | string | `T-###`，全局顺序递增，**一经分配永不复用** |
| `title` | string | 动词开头，一句话 |
| `dimension` | enum | `architecture`=架构 / `function`=功能 / `ux`=UX / `simplicity`=技术简洁 |
| `priority` | enum | `P0`/`P1`/`P2`/`P3` |
| `status` | enum | 见 4.4 |
| `batch` | int | 所属批次号 |
| `findingId` | string | 来源问题 `F-###`（T→F 单向追溯，见 4.8） |
| `goalRef` | string/null | **关联目标 `G-###`（v4 新增）**；差距任务必填，finding 任务可 null；非空时自检对照 `goals.json` 可解析。**executor/verifier 忽略此字段**（目标达成由度量驱动） |
| `dependsOn` | string[] | 前置任务 id（可为空） |
| `location` | string | 证据位置 `file:line` |
| `scope` | string | 改动范围（项目/文件/模块） |
| `requirements` | string[] | 动作清单（每条动词开头、可执行，禁止"优化/完善"类空话） |
| `goal` | string | 完成后形态（可衡量） |
| `acceptanceCriteria` | object[] | `{ text, verification: "自动"\|"手动", command? }`，非空 |
| `attempts` | int | 打回重试次数，初始 0，验收打回 +1 |
| `statusReason` | string/null | 打回/终态原因（打回、parked、cancelled、problem 时必填） |
| `note` | string/null | 执行者改动记录与证据 / 验收结论 / 批注 |
| `updatedAt` | string/null | 状态最后变更日期 `YYYY-MM-DD` |

**目标字段表**（`goals.json`，v4 新增）：

| 字段 | 类型 | 约束 |
|---|---|---|
| `id` | string | `G-01` 顺序递增，**一经分配永不复用** |
| `level` | enum | `vision`（愿景级，不量化）/ `domain`（能力域，可量化可不量化）/ `metric`（量化指标，默认，leaf） |
| `parent` | string/null | 父目标 id（vision 级必须 null；层级中用于组织与进度派生） |
| `benchmark` | object/null | 对标基准 `{ reference, basis, source }`——**描述性参照，不编造数值**；target 由用户拍板 |
| `category` | enum | **产品阶段分类 `function`（功能）/ `performance`（性能）/ `polish`（美化）**；metric 级目标必填（自检强制），vision/domain 组织层可空；优先级 `function > performance > polish` |
| `title` | string | 一句话，**含目标值** |
| `metric` | object | `{ name, unit }` 指标名与单位 |
| `kbRef` | object/null | **支撑目标判据的知识库条目 `{ file, section }`（v4 新增）**；`file` 为知识库相对路径、`section` 为章节引用（如 `§6 设置/配置交互`，对齐 `文件 §N` 先例）；**assess 目标必填（自检强制），command 可空** |
| `measure` | object | 度量方式二选一：`{ type:"command", command, expected, valueFrom }` 或 `{ type:"assess", rubric, scale, valueFrom }`（组织层可空）；assess 的 `rubric` = **kbRef 判据 + 本目标特化约束**，判据本体在知识库章节，非完全内嵌 |
| `direction` | enum | `down`/`up`/`flat`（currentValue 与 target 比较方向；组织层可空） |
| `baseline` | number/null | 设定时当前值；不可测 null 待首轮度量回填 |
| `target` | number | 目标值（组织层可空） |
| `currentValue` | number/null | 最近度量值（`archive.py --goals` 合并回填）；未测 null |
| `progress` | array/null | **每轮度量历史 `[{ batch, currentValue, measuredAt }]`（v4 新增）**；由 `archive.py --goals` 合并时自动追加（幂等）——停滞/抖动/判据失效检测的数据源 |
| `status` | enum | `active`/`achieved`/`parked`/`archived` |
| `achievedCondition` | string | 置 achieved 的条件（command：命令全绿 + 达标；assess：rubric 判定 + 证据 + **人工确认**） |
| `relatedTasks` | string[] | 关联任务 id（goalRef 反查来源，可空） |
| `createdBatch` | int | 设定批次号 |
| `lastMeasuredAt` | string/null | 最近度量日期 |
| `measureNote` | string/null | 度量证据；**assess 度量后必填**（自检强制） |

**层级语义（v4）**：`vision`（对标声明，如「对标 Google Drive 同步体验」）→ `domain`（能力域分组，如「按需流式传输」）→ `metric`（量化指标，leaf）。**收敛判定与差距任务只看 leaf**（无子目标的 metric/domain）；vision/domain 是组织层，进度由子目标派生。`level` 缺省为 `metric`（扁平目标兼容）。

## 4.3 任务编号

- 全局顺序：`T-001`、`T-002`…跨维度连续递增
- 编号只在追加新任务时分配；**永不复用、永不重排**
- 编号延续：新批次从 `meta.json` 现有最大任务号 +1 起（从 `tasks-index.json` 取最后一个 id）

## 4.4 状态生命周期

```
todo → in-progress → acceptance → done
  │       │
  │       └→ todo（验收打回，attempts+1，记 statusReason）
  │       └→ problem（attempts > 3，超重试上限，回炉重新定义）
  └──────→ parked / cancelled（终态，记 statusReason）
```

- 状态推进者：`todo→in-progress→acceptance` 由 **task-executor**（写 active 卡）；`acceptance→done|todo|problem` 由 **task-verifier**（写 active 卡）
- `acceptance → done` 必须由验收者**独立裁决**，执行者不得自验
- done 卡归档（active → history）由 `/mission` 批次收尾统一执行，executor/verifier 不负责
- **state.json 是派生视图，active 卡是真值**：executor/verifier 只写卡；`archive.py` 在批次收尾自动 `sync_state` 使 state 与卡一致

## 4.5 优先级（P0-P3）

- **P0 阻塞**：直接破坏核心功能或违反硬性规则（架构反向依赖、数据丢失风险）
- **P1 高**：明显影响核心体验或显著偏离使命
- **P2 中**：改进项，值得做但可排队
- **P3 低**：远期优化或锦上添花

## 4.6 数量约定（强制）

1. **每批次任务 ≤ 12 个**。超出时在批次结论中说明被截断，未纳入的进下一批
2. **P0+P1 合计 ≤ 6**，超限按影响面排序取前 6，其余进下批
3. **单任务必须能一次独立改动完成**（≈ 一个 PR）。超限 → 拆分为多个任务（共享同一 `findingId`）
4. **不凑数**：某维度未发现 P0/P1 时，`dimensionSummary` 明说「未发现 P0/P1」；写不出验收标准的任务退回 Step 2 补充
5. **不重复**：先在 `contract/tasks-index.json` 检索 `title`/`location` 去重

## 4.7 验收标准规范（含 P0-B 对抗性要求）

- 每条以动词开头、**可测试**，禁止"确保""正常"类空话
- 必须标注**验证方式**：`自动`（附具体命令/测试）或 `手动`（清单项）
- **禁止「grep 无匹配」类空转 AC 作为唯一验证**——这类 AC 无论实现成什么样都大概率通过，让验收无法打回。每个任务至少一条 **行为断言** AC：
  - 功能/架构：具体场景 + 期望输出（「上传→版本回滚内容为旧版本」「重命名后版本历史可查新路径」）；有测试则 `command: dotnet test --filter {测试名}`
  - 简洁：量化（「删除 ≥ N 行冗余代码」「重复实现收敛为单一」）
  - UX：可观察行为（「首次配置 ≤ 3 步」）
- **对抗性自问**（写 AC 时）：如果实现得一团糟，这个 AC 会不会还是通过？会 → 换更强的断言
- 高风险任务（DB+FS/事务/跨模块/并发）至少一条 AC 直接检验风险点（如「DB 回滚后临时文件已清理」）

## 4.8 追溯

- 契约只存单向 `task.findingId`（T→F）；F→T 由渲染视图反查，**不存** `finding.taskId`（支持一 F 多 T）
- 跨维度冲突在任务卡 `note` 中显式标注取舍

## 4.9 契约自检（每次写入前）

- [ ] JSON 可解析，`meta.json` `schemaVersion` = **4**、`goals.json` `schemaVersion` = **4**
- [ ] `id` 全局唯一（对照 `tasks-index.json`）、`findingId` 可解析（对照 `findings-index.json`）
- [ ] `dimension`/`priority`/`status`/`verification` 枚举合法
- [ ] `acceptanceCriteria` 非空，且每条含 `text`+`verification`，且至少一条是行为断言
- [ ] 非空 `goalRef` 对照 `goals.json` 可解析；差距任务 `goalRef` 非空
- [ ] 打回/parked/cancelled/problem 时 `statusReason` 非空
- [ ] 写入后跑 `python docs/task-matrix/tools/archive.py --check` 通过

## 4.10 审查结果交接（.reviews/）

- 四维审查子 Agent 由指挥层 `/mission` **直接分发**（producer 不自行分发），发现写入 `docs/task-matrix/.reviews/{dimension}.json`
- 元素结构：`{ dimension, severity, location, problem, why, suggestion }`，每维度 ≤15 条，按严重度排序
- **目标度量（独立环节，与审查解耦）**：审查子 Agent 只找问题不度量；目标当前值由指挥层**独立分发度量子 Agent**（按 `category` 分 3 个：function/performance/polish）写入 `docs/task-matrix/.reviews/goals/{category}.json`（`{ "category": "{类}", "goals": [ { id, currentValue, measured, measureNote, lastMeasuredAt } ] }`）；指挥层用 `archive.py --goals` 合并进 `goals.json` 后 producer 才读——**目标差距以 `goals.json` 的合并值为准**，`.reviews/goals/` 作证据
- producer **只读**这些文件汇总；发现文件缺失即停止报告，不得自行补发审查（缺失维度由指挥层重试）
- `.reviews/` 是批次的输入证据（F-id 溯源源），批次产出后保留作审计线索；下次补批由新审查覆盖
