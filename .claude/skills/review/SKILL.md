# /review — 三维度代码审查

对当前 git 变更执行三个独立维度的自动化审查，综合输出结果。

## 触发条件

- 用户输入 `/review`
- 用户说"审查一下"、"review 一下"、"检查代码"

## 执行流程

### Step 1: 获取变更范围
运行 `git diff --name-only` 获取变更文件列表。若无变更，告知用户并退出。

### Step 2: 启动三个审查 Agent（并行）
同时启动三个审查 Agent，每个专注一个维度：

1. **code-reviewer-order** — 顺序与依赖：中间件注册顺序、DI 生命周期、事务边界、DB+FS 一致性
2. **code-reviewer-concurrency** — 并发与生命周期：Timer 回调、async void、fire-and-forget、IDisposable、字段线程安全
3. **code-reviewer-exception** — 异常与恢复：catch 块正确性、AggregateException、回滚、资源清理

使用 Agent 工具，subagent_type 分别为 `code-reviewer-order`、`code-reviewer-concurrency`、`code-reviewer-exception`。

Prompt 模板（发送给每个 Agent）：
```
请审查当前 git diff 中的 C# 文件变更。运行 `git diff` 获取完整 diff 内容，然后按照你的审查维度逐文件检查。输出格式按照你的 agent 定义中的规范。
```

### Step 3: 汇总输出
收集三个 Agent 的返回结果，汇总为统一报告：

```
═══════════════════════════════════════════
           CloudPan 代码审查报告
═══════════════════════════════════════════

📋 审查范围：N 个文件变更

🔴 顺序与依赖
（code-reviewer-order 的结果）

🟠 并发与生命周期
（code-reviewer-concurrency 的结果）

🟡 异常与恢复
（code-reviewer-exception 的结果）

═══════════════════════════════════════════
总计：X 个 Critical / Y 个 Warning
═══════════════════════════════════════════
```

### Step 4: 风险提示
对 Critical 问题给出简短的一句话说清楚"不改会导致什么"。

## 文件关联

审查 Agent 定义：
- `.claude/agents/code-reviewer-order.md`
- `.claude/agents/code-reviewer-concurrency.md`
- `.claude/agents/code-reviewer-exception.md`
