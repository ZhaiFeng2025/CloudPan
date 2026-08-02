# /check — 编译 + 分析器 + 架构测试

一键运行所有静态检查，确保代码质量门槛。

## 触发条件

- 用户输入 `/check`
- 用户说"检查编译"、"跑一下检查"、"验证代码"

## 执行流程

### Step 1: 契约代码生成校验
```bash
cd e:/XiaoFeng/云盘 && dotnet run --project CloudPan.CodeGen -- --verify
```
若校验失败，说明 shared-spec.json 与生成代码不一致，需要重新生成。

### Step 2: 全量编译（含分析器）
```bash
cd e:/XiaoFeng/云盘 && dotnet build CloudPan.sln -c Release -p:TreatWarningsAsErrors=true 2>&1
```
> **必须配 `-c Release`**：`WarningsNotAsErrors`（CP301/CP200/CP303）豁免仅在 Release 配置生效；
> 裸 `-warnaserror`（Debug）会把 Client.UI 中既有的 CP301（匿名 lambda 事件订阅）误升级为 error 阻塞编译。
> 此命令与 `.github/workflows/ci.yml` 的 CI 门禁一致。

分析器已在编译时运行（CP001-CP401 系列），编译失败会在此步暴露。

特别关注：
- CP302: Timer async lambda
- CP400: 中间件顺序错误
- CP401: Fire-and-forget
- CP304: 事件订阅未实现 IDisposable

### Step 3: 单元测试
```bash
cd e:/XiaoFeng/云盘 && dotnet test CloudPan.Tests --no-build -v minimal 2>&1 | tail -20
```

### Step 4: 输出结果
```
═══════════════════════════════════════════
           CloudPan 质量检查报告
═══════════════════════════════════════════

✅ / ❌ 契约代码生成
✅ / ❌ 编译（含 Roslyn 分析器）
✅ / ❌ 单元测试（N passed / M failed / S skipped）

═══════════════════════════════════════════
```
某项失败时给出下一步操作建议。

## 文件关联

- `CloudPan.CodeGen/` — 契约代码生成器
- `CloudPan.Analyzers/` — Roslyn 分析器（编译时自动执行）
- `CloudPan.Tests/` — 测试项目
- `shared-spec.json` — 契约定义
