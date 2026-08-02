# /mission — 任务集群自循环驱动

一次 `/mission` 内，三 Agent 集群（**产生 → 执行 → 验收**）**自循环**处理任务，直到收敛或命中安全上限，**无需反复触发**。契约（tasks.json）是持久化状态，可随时中断、随时续跑。

## 触发

- `/mission` — 自循环，默认处理上限 **1000 个任务**（硬性兜底；实际以收敛/质量护栏为准）
- `/mission N` — 本轮处理上限 N 个任务
- `/mission --produce` — 只调 task-producer 补批
- `/mission --verify` — 只验收全部 `acceptance` 任务
- 用户说"启动任务集群 / 自循环 / 继续任务"

## 终止条件（任一命中即停止，保证不失控）

| # | 条件 | 说明 |
|---|---|---|
| 1 | **收敛** | producer 补批 0 新任务，且可领取清空 → 四维干净，使命阶段性达成 |
| 2 | **安全上限** | 本轮处理任务数 ≥ N（默认 1000）→ 契约持久化，继续 /mission 续跑 |
| 3 | **连续 3 打回** | 连续 3 次验收失败 → 任务定义或执行存在系统性问题，交 producer 回炉 |
| 4 | **单任务重试超限** | 某任务 attempt > 3 → `problem`，交 producer 回炉 |
| 5 | **依赖阻塞** | 可领取清空、U > 0 且 producer 无新任务 → 报告依赖链 |
| 6 | **用户中断** | 你说"停 / stop"，或会话被终止 |

## 主循环（单次 /mission 内自动进行）

### Step 0 初始化
- 读 `docs/task-matrix/tasks.json`，校验 `schemaVersion=2`；不存在 → 先调 producer 产生第一批
- 输出：初始 U（未完成）、可领取数、本轮上限 N

### Step 1 处理一波（wave）
1. 取可领取列表（`todo` 且 `dependsOn` 全 done；**本轮未尝试者优先**，全部尝试过才重取已打回任务）
2. 逐任务（**显式传任务 ID**）：
   - 调 `task-executor` → 实现 + 自证 + **本地 commit（T-###）** + `status → acceptance`
   - 调 `task-verifier` → 验收：`done` / 打回（`todo`, attempts+1）/ 留 `acceptance`（手动项，不阻塞循环）
3. 质量护栏：连续 3 打回或单任务 attempt>3 → 停止（条件 3/4）
4. **每 20 个任务输出 checkpoint**：已处理 X/N、done/打回/待确认数、当前 U——供你随时中断

### Step 2 补批（自循环关键）
本波可领取清空后：若 **U < 3** → 调 `task-producer` 补批
- 0 新任务 → **收敛**，进入 Step 3
- 有新任务 → 回到 Step 1 继续（**循环直到命中终止条件**）

### Step 3 收尾
- 重渲染 `docs/task-matrix/INDEX.md`（契约 → 视图，逐字一致）
- 汇总报告：
  - 本轮处理 T 任务：done M / 打回 L / 待人工确认 P / problem Q
  - **commit 清单**（`T-### → commit hash`，未 push）
  - **待人工确认项**（手动验收项，你确认后置 done）
  - **problem/blocker 项**（提示 producer 回炉）
  - 停止原因（收敛 / 上限 / 质量告警 / 阻塞）
- 达到上限 → 提示"继续 /mission 续跑"；收敛 → 使命阶段性达成

## 关键机制

### 每任务本地 commit每任务本地 commit（自循环可运行的前提）
- 执行者在实现+自证后**本地 commit**：消息 `T-###: {标题}`（中文），**不 push**
- 理由：多任务连续执行必须逐任务干净——验收针对已提交状态、回滚=revert commit、commit 历史按任务可追溯
- 打回重做：执行者改后追加 commit（或 amend），`note` 记录 attempts
- **你调用 /mission 即授权执行者做本地提交；push 始终由你执行**

### 验收分流（verifier）
- 自动标准全过 + 无手动项 → `done`
- 自动标准全过 + 含手动项 → 留 `acceptance`，note 标「需人工确认」，**不阻塞循环**，你确认后置 done
- 任一自动项未过 → 打回（`todo` + attempts+1）

### 公平调度
- 领取优先本轮未尝试任务，防单个 P0 打回任务饿死其他任务
- 全部尝试过后才重取打回任务（重做有 attempts 上限护栏）

### 质量护栏
- 连续 3 打回 → 停止：任务定义质量差，交 producer 回炉
- 单任务 attempt>3 → `problem`：回炉
- 本轮 done 数 = 0 且处理 ≥ 5 任务 → 停止：系统性执行/定义问题

## 维护约定

- **契约（tasks.json）唯一事实来源**；INDEX/批次文档为渲染视图，禁止手改
- `problem` 任务先由 task-producer 回炉，不得由执行者直接修
- 跨任务不得改契约非本任务字段
- 集群 Agent：`.claude/agents/task-producer.md` / `task-executor.md` / `task-verifier.md`；知识库：`.claude/knowledge/`
