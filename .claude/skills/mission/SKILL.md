# /mission — 任务集群自循环驱动（契约 v4：目标 + 分片）

一次 `/mission` 内，三 Agent 集群（**产生 → 执行 → 验收**）**自循环**处理任务，直到**目标达成**或命中安全上限，**无需反复触发**。契约（`docs/task-matrix/contract/`）是持久化状态，可随时中断、随时续跑。

**目标导向**：使命「四性」量化到 `contract/goals.json`（目标 + 指标 + 基线 + 目标值），审查子 Agent 顺带度量指标当前值，差距产出**差距任务**；收敛以「目标达成」为主导语义——目标达成由**度量驱动**，不由「任务 done」驱动（防止「任务全绿但目标未达」的假收敛，v1.0 批次 9 曾出现）。

## 契约结构（v4 目标 + 分片）

```
docs/task-matrix/
├── contract/                   # 契约（唯一事实来源）
│   ├── meta.json               # schemaVersion=4 / currentBatch / 统计
│   ├── state.json              # 活跃任务摘要（可领取列表来源）
│   ├── goals.json              # ★ 目标契约：愿景→能力域→量化指标 三层（level/parent/benchmark 对标基准）
│   ├── active/T-{id}.json      # 单任务完整卡（含 goalRef；executor/verifier 只读写本卡）
│   ├── history/batch-{NN}.json # 已闭合批次完整任务（归档）
│   ├── findings.json           # 全部 findings（T→F 追溯）
│   └── tasks-index.json        # 全部任务一行摘要（含 goalRef；渲染 INDEX + 跨批次去重）
├── .reviews/                   # 审查交接：{dim}.json 发现 + goals/{dim}.json 度量
├── INDEX.md                    # 渲染视图：活状态板 + 目标面板（禁止手改）
└── batches/batch-{NN}.md       # 渲染视图：批次完整文档（禁止手改）
```

**读写边界（效率关键，禁止越界）**：

| 角色 | 读 | 写 |
|---|---|---|
| executor / verifier | 仅对应 `active/T-{id}.json` + 相关代码 | 仅本卡（status/note/attempts） |
| producer | `tasks-index.json`（去重）+ `findings.json` + `state.json` + `goals.json`（目标差距） | 新批次：`history/batch-{NN}.json` + 新卡 `active/T-{id}.json`（含 `goalRef`）+ `state.json` 追加 + `tasks-index.json`/`findings.json` 追加 |
| 审查子 Agent | 知识库 + `goals.json`（仅本维度 `status=active` 的目标） | `.reviews/{dim}.json` 发现 + `.reviews/goals/{dim}.json` 度量 |
| 指挥层 | `meta.json` + `state.json` + `tasks-index.json` + `goals.json` | 合并度量（`archive.py --goals`）+ 目标设定（`--goals`）+ 归档渲染（`archive.py`） |

> executor/verifier **禁止读 history/ 与 tasks-index.json**——单任务卡 ~600 token，全量索引是去重用，不是执行用。这是 v3 分片的核心收益，v4 沿用（v2 的 tasks.json 690KB ≈ 16.6 万 token，占子 Agent 窗口 83%）。

## 触发

- `/mission` — 自循环，默认处理上限 **1000 个任务**（硬性兜底；实际以收敛/质量护栏为准）
- `/mission N` — 本轮处理上限 N 个任务
- `/mission --goals` — **目标设定/刷新**（三步引导）：① 设愿景（`level=vision`，对标声明，不量化）→ ② 拆能力域（`level=domain`，带 `benchmark` 对标基准）→ ③ 拆量化指标（`level=metric`，target 用户拍板）→ 写入 goals.json → 渲染 INDEX（不执行任务）
- `/mission --produce` — 只补批（分发四维审查（含目标度量）→ 合并度量 → task-producer 产出，不执行）
- `/mission --verify` — 只验收全部 `acceptance` 任务
- 用户说"启动任务集群 / 自循环 / 继续任务"

## 终止条件（任一命中即停止，保证不失控）

| # | 条件 | 说明 |
|---|---|---|
| 1 | **目标收敛**（主导） | producer 补批 0 新任务 + 可领取清空 + **全部 leaf 级目标达成**（无子目标的 metric/domain，currentValue 达 target 且 measureNote 有证据）→ 使命达成；vision/domain 为组织层，由子目标派生，**不直接判定** |
| 1a | **目标未达空转** | 补批 0 新任务 + 可领取清空 + **存在未达 leaf 级目标** → 停止本轮，报告「目标未达：G-xx 当前 X/目标 Y，已无可执行差距任务」，**不标收敛**，等新目标/人工度量 |
| 1b | **无目标降级** | `goals.json` 无 `active` 目标 → 不判定「目标收敛」，报告「无活跃目标，先 /mission --goals 设定」；仍按找问题逻辑跑，收敛语义显式标注「无目标降级」而非「目标达成」 |
| 2 | **安全上限** | 本轮处理任务数 ≥ N（默认 1000）→ 契约持久化，继续 /mission 续跑 |
| 3 | **连续 3 打回** | 连续 3 次验收失败 → 任务定义或执行存在系统性问题，交 producer 回炉 |
| 4 | **单任务重试超限** | 某任务 attempt > 3 → `problem`，交 producer 回炉 |
| 5 | **依赖阻塞** | 可领取清空、U > 0 且 producer 无新任务 → 报告依赖链 |
| 6 | **目标停滞护栏** | 某 `active` 目标**连续 2 轮** currentValue 无变化且无新差距任务 → 报告「G-xx 目标或度量定义需复查」并停止该目标相关循环 |
| 7 | **用户中断** | 你说"停 / stop"，或会话被终止 |

## 主循环（单次 /mission 内自动进行）

### Step 0 初始化
- 读 `contract/meta.json`，校验 `schemaVersion=4`；读 `contract/state.json` 得活跃任务摘要；读 `contract/goals.json` 得目标集（含层级）
- 输出：初始 U（未完成）、可领取数、本轮上限 N、**目标面板**（层级：vision 显示「子x/y」+ 各 leaf 的 currentValue/target/status）
- 若全部 **leaf 级**目标已 `achieved` 且可领取为空 → 直接进入 Step 3，报告「目标已达成」（vision/domain 由子目标派生，不单独判定）

### Step 1 处理一波（wave）
1. 取可领取列表（`state.json` 中 `status=todo` 且 `dependsOn` 全 done；**本轮未尝试者优先**，全部尝试过才重取已打回任务）
2. 逐任务（**显式传任务 ID 与任务卡路径** `contract/active/T-{id}.json`）：
   - 调 `task-executor` → 读任务卡 → 实现 + 自证 + **本地 commit（T-###）** + 写卡 `status → acceptance`
   - 调 `task-verifier` → 读任务卡 → 验收：写卡 `done` / 打回（`todo`, attempts+1）/ 留 `acceptance`（手动项，不阻塞循环）
3. 质量护栏：连续 3 打回或单任务 attempt>3 → 停止（条件 3/4）
4. **每 20 个任务输出 checkpoint**：已处理 X/N、done/打回/待确认数、当前 U——供你随时中断

### Step 2 补批（自循环关键）
本波可领取清空后：若 **U < 3** → 补批：

1. **指挥层直接分发四维审查子 Agent**（并行，subagent_type: general-purpose），每个维度除写发现 `docs/task-matrix/.reviews/{dimension}.json` 外，**顺带度量本维度 `active` 目标当前值**写入 `docs/task-matrix/.reviews/goals/{dimension}.json`——分发 Prompt 见下方「审查子 Agent 分发模板」与「维度→知识库映射」
2. 等四个维度审查全部完成（**指挥层接收结果，不嵌套**）
3. **指挥层合并度量**：跑 `python docs/task-matrix/tools/archive.py --goals`——把 `.reviews/goals/` 结果合并回 `goals.json`（currentValue/lastMeasuredAt/measureNote，达标自动置 `achieved`）+ 重渲染 INDEX
4. 调 `task-producer`（subagent_type: task-producer）：读 `.reviews/` + `goals.json` **两步产批**：
   - **差距任务**（目标驱动）：对每个未达 `active` 目标，按 `tasks-index.json` 的 `goalRef` 去重，若该目标无未闭合差距任务 → 产出差距任务并标 `goalRef`（即便审查无任何新 finding 也要产出——目标不再依赖「审查是否发现新问题」）
   - **finding 任务**（缺陷驱动）：对四维发现汇总去重产出
   - 写入 `contract/`（历史批次文件、新 active 卡含 `goalRef`、state.json 追加、tasks-index/findings 追加）+ 渲染批次文档
5. 新批次 0 任务 → **收敛判定分流**（进入 Step 3 时按终止条件 1/1a/1b 报告）：
   - 全部 `active` 目标达成 → 条件 1「目标收敛」
   - 存在未达 `active` 目标 → 条件 1a「目标未达空转」
   - 无 `active` 目标 → 条件 1b「无目标降级」
   - 有新任务 → 回到 Step 1 继续

**审查子 Agent 分发模板**（发给每个维度审查子 Agent，替换「{维度}」「{知识库}」）：
```
你是 /mission 派出的「{维度}」维度审查 Agent。
背景：自托管家庭云盘（C#/.NET8 WinForms + Kotlin Android + ASP.NET Core 8 + SQLite）。
第一步（必做）：先 Read 「{知识库}」，按其审查问题清单逐条扫描。
第二步：全库扫描（不是 git diff），发现阻碍「最佳架构/合理功能/最佳UX/简洁技术」使命的具体问题。
第三步（必做·目标度量）：读 docs/task-matrix/contract/goals.json，取 dimension="{维度}" 且 status=active 的目标逐条度量当前值：
  - measure.type=command → 运行其 command，核对 expected 后按 valueFrom 计算 currentValue；命令无法运行则 measured=false 并说明
  - measure.type=assess → 依 measure.rubric 走查判定 currentValue，measureNote 必填证据（file:line / 复现路径 / 步骤清单）
  - 结果写 docs/task-matrix/.reviews/goals/{维度}.json：
    { "dimension": "{维度}", "goals": [ { "id": "G-01", "currentValue": 8, "measured": true, "measureNote": "...", "lastMeasuredAt": "YYYY-MM-DD" } ] }
  - 禁止直接写 contract/goals.json（由指挥层 archive.py --goals 统一合并，防并发写冲突）
要求：
1. 先读 CLAUDE.md 了解约束
2. 每条发现：{ "dimension": "{维度}", "severity": "P0|P1|P2|P3", "location": "file:line", "problem": "现状与问题", "why": "为什么阻碍使命", "suggestion": "方向性建议" }
3. 只读，禁止修改任何源码文件
4. 把全部发现（≤15 条，按严重度排序）写入 `docs/task-matrix/.reviews/{维度}.json`（JSON 数组），最终回复附简要总结
```

**维度→知识库映射**：
| 维度 | 知识库 |
|---|---|
| architecture | `.claude/knowledge/architecture-kb.md` |
| function | `.claude/knowledge/feature-kb.md` + `.claude/knowledge/clouddrive-kb.md`（产品形态参照） |
| ux | `.claude/knowledge/ux-kb.md` + `.claude/knowledge/clouddrive-kb.md` + `.claude/knowledge/visual-design-kb.md` |
| simplicity | `.claude/knowledge/simplicity-kb.md` |

### Step 3 收尾
1. **归档**：跑 `python docs/task-matrix/tools/archive.py`——把本轮 `done` 的 active 卡移入 `history/batch-{NN}.json`、从 `state.json` 移除、更新 `tasks-index.json`、删除已归档卡、重渲染 `INDEX.md`（全自动，幂等）
2. 汇总报告：
  - 本轮处理 T 任务：done M / 打回 L / 待人工确认 P / problem Q
  - **commit 清单**（`T-### → commit hash`，未 push）
  - **目标推进**：各 `active` 目标 G-xx 的 currentValue/target 变化（含 `achieved` 目标）；存在未达目标时明说「目标未达」与差距
  - **待人工确认项**（手动验收项 + assess 类已达标目标的「待人工确认」）
  - **problem/blocker 项**（提示 producer 回炉）
  - 停止原因（**目标收敛** / **目标未达空转** / **无目标降级** / 上限 / 质量告警 / 阻塞 / 停滞护栏）
3. 达到上限 → 提示"继续 /mission 续跑"；目标收敛 → 使命达成；目标未达 → 提示「/mission --goals 调整目标或补充度量」

## 关键机制

### 目标与任务解耦（v4 核心）
- **目标达成由度量驱动，不由任务 done 驱动**：任务的 `goalRef` 只是「服务哪个目标」的元数据；目标是否 `achieved` 取决于 `archive.py --goals` 合并的度量结果（currentValue 达 target），**不是**「该目标下所有任务 done」
- 因此允许「任务 done 但目标未达」中间态并**显性报告**——这正是对 v1.0 批次 9（108 任务全绿但行数门禁红灯）假收敛的机制性回应
- **层级（vision→domain→metric）**：vision 是产品愿景（对标声明，不量化）、domain 是能力域分组（可量化也可不量化）、metric 是量化指标（leaf）。**收敛判定只看 leaf**——vision/domain 进度由子目标派生（面板显示「子x/y」），不直接判定
- executor/verifier 不感知 goalRef，goal 推进不增加它们的工作量

### 每任务本地 commit（自循环可运行的前提）
- 执行者在实现+自证后**本地 commit**：消息 `T-###: {标题}`（中文），**不 push**；**实现代码与任务卡状态变更（active/T-{id}.json）合并为一个 commit**
- 理由：多任务连续执行必须逐任务干净——验收针对已提交状态、回滚=revert commit、commit 历史按任务可追溯；任务卡独立于全量契约，revert 实现 commit 不会连带历史批次
- 打回重做：执行者改后追加 commit（或 amend），`note` 记录 attempts
- **你调用 /mission 即授权执行者做本地提交；push 始终由你执行**

### 验收分流（verifier）
- 自动标准全过 + 无手动项 → `done`（写卡 status=done）
- 自动标准全过 + 含手动项 → 留 `acceptance`，note 标「需人工确认」，**不阻塞循环**，你确认后置 done
- 任一自动项未过 → 打回（`todo` + attempts+1）
- **对抗性**（P0-B）：高/中风险任务（见 verifier 定义）除逐条跑 AC 外，独立复查实现代码；发现 AC 之外的实质缺陷即打回

### 公平调度
- 领取优先本轮未尝试任务，防单个 P0 打回任务饿死其他任务
- 全部尝试过后才重取打回任务（重做有 attempts 上限护栏）

### 质量护栏
- 连续 3 打回 → 停止：任务定义质量差，交 producer 回炉
- 单任务 attempt>3 → `problem`：回炉
- 本轮 done 数 = 0 且处理 ≥ 5 任务 → 停止：系统性执行/定义问题
- **打回率归零预警（P0-B）**：本轮 done > 5 且打回 = 0 → 指挥层抽查 1–2 个已 done 任务做独立复核（读实现代码对照 AC），复核发现缺陷即打回并记 `note`——防止验收走过场

## 维护约定

- **契约唯一事实来源 = `docs/task-matrix/contract/`**（含 `goals.json` 目标契约）；INDEX/批次文档为渲染视图，禁止手改
- **聚合操作用脚本**：归档+渲染 = `python docs/task-matrix/tools/archive.py`；**目标度量合并 = `python docs/task-matrix/tools/archive.py --goals`**；v2→v3 迁移 = `migrate-v2-to-v3.py`；**v3→v4 迁移 = `python docs/task-matrix/tools/migrate-v3-to-v4.py`**
- **审查交接用 `.reviews/*.json`**：四维审查由指挥层直接分发（producer 不自行分发，防嵌套回传死锁）；发现与目标度量分离——发现写 `.reviews/{dim}.json`，度量写 `.reviews/goals/{dim}.json`（禁止审查子 Agent 直接写 `goals.json`，防并发写冲突）
- **目标设定用 `/mission --goals`**：指挥层与用户确认目标后写入 `goals.json`；assess 类目标必含 `measure.rubric`；目标 `status` 的 `parked/archived` 仅由 `--goals` 人工设定；**层级字段** `level`（vision/domain/metric）+ `parent` + `benchmark`（对标基准，描述性参照不编造数值）由 `--goals` 三步引导填写，`level` 默认 `metric`（扁平目标兼容）
- `problem` 任务先由 task-producer 回炉，不得由执行者直接修
- 跨任务不得改契约非本任务字段（executor/verifier 只碰自己的 active 卡）
- 集群 Agent：`.claude/agents/task-producer.md` / `task-executor.md` / `task-verifier.md`；知识库：`.claude/knowledge/`
