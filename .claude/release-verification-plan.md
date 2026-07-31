# CloudPan 发布工程化验收方案

> **版本**: v2.0（对抗审查修订版）
> **修订日期**: 2026-07-31
> **适用阶段**: v1.0.0 正式发布版及后续版本
> **状态**: ⏳ 待审查
>
> **v2.0 修订说明**（基于对抗审查 v1.0 → v2.0）：
> - 从 Phase 0 标准升级为 v1.0.0 正式发布标准
> - 新增：Git 工作区清洁门禁、数据库升级测试、UDP 发现测试、
>   WebSocket 全生命周期、文件系统边界条件 6 类、隐私验证、回滚验证、
>   依赖许可合规、浸泡测试
> - 修复：确定性构建改用 SHA 比对、版本号逻辑修正为严格相等、
>   覆盖率阈值全面上调、Console.WriteLine 扫描范围收紧、
>   Skip 标准改为文档化审查、依赖方向错误升级为阻断
> - 脚本：Bash → PowerShell（Windows 原生可用），不再依赖 jq

---

## 验收流水线总览

```
Stage 0: 工作区清洁  ──→  Stage 1: 契约一致性  ──→  Stage 2: 静态分析
Stage 3: 编译        ──→  Stage 4: 单元测试      ──→  Stage 5: 架构门禁
Stage 6: 覆盖率      ──→  Stage 7: 性能基准      ──→  Stage 8: 集成测试
Stage 9: 安全审查    ──→  Stage 10: 打包与发布   ──→  Stage 11: 最终清单
```

**阻断级别说明**：

| 级别 | 含义 | 触发动作 |
|------|------|---------|
| 🔴 **阻断** | 不通过则禁止发布 | 修复后从头重跑全流程 |
| 🟡 **条件通过** | 需要文档化+审批 | Release Notes 中披露，记录豁免原因 |
| 🟢 **通过** | 自动放行 | — |

---

## Stage 0: 工作区清洁验证

**目标**: 发布必须是已知源码状态的精确快照，不允许存在未跟踪或未提交的变更。

### 0.1 Git 工作区清洁

```powershell
# PowerShell 执行（Windows 原生）
$porcelain = git status --porcelain
if ($porcelain) {
    Write-Host "❌ 阻断：工作区不清洁，存在未提交变更或未跟踪文件："
    Write-Host $porcelain
    exit 1
}
Write-Host "✅ 工作区清洁"
```

| 检查项 | 通过标准 | 阻断 |
|--------|---------|------|
| `git status --porcelain` | 零输出（空字符串） | 🔴 |
| untracked 文件 | 必须全部 `.gitignore` 或提交 | 🔴 |
| modified 文件 | 必须全部提交 | 🔴 |

### 0.2 未跟踪文件审计

```powershell
# 列出所有未跟踪文件，逐项审核：应提交 / 应 .gitignore / 应删除
$untracked = git ls-files --others --exclude-standard
if ($untracked) {
    foreach ($f in $untracked) {
        Write-Host "⚠️  未跟踪: $f"
    }
    Write-Host "请确认以上文件已加入 .gitignore 或已提交"
}
```

| 典型可疑文件 | 处理方式 |
|-------------|---------|
| `*.user` | 加入 `.gitignore` |
| `bin/` `obj/` | 已在 `.gitignore`（验证不出现） |
| `TestResults/` | 加入 `.gitignore` |
| `*.suo` `*.vs/` `.idea/` | 加入 `.gitignore` |

### 0.3 当前分支与 Tag 对齐

```powershell
$branch = git branch --show-current
$lastTag = git describe --tags --abbrev=0 2>$null
Write-Host "当前分支: $branch"
Write-Host "最近 Tag:  $lastTag"
# 发布必须基于 main 分支
if ($branch -ne "main") { Write-Host "❌ 阻断：不在 main 分支"; exit 1 }
```

---

## Stage 1: 契约一致性验证

**目标**: 确保所有生成代码与 `shared-spec.json` 完全一致，零手工翻译。

### 1.1 代码生成器校验模式

```powershell
dotnet run --project CloudPan.CodeGen -- --verify
if ($LASTEXITCODE -ne 0) {
    Write-Host "❌ 阻断：代码生成器校验失败"
    exit 1
}
```

| 项目 | 通过标准 | 阻断 |
|------|---------|------|
| 退出码 | `0` | 🔴 |
| 输出行 | 全为 `✅ XXX: 一致` 或 `⏭️ XXX: 无变更` | 🔴 |
| 生成文件 | Enums.g.cs / Dtos.g.cs / Entities.g.cs / ContractManifest.g.cs / ErrorResponse.g.cs / ApiResponses.g.cs 全部校验通过 | 🔴 |

修复步骤：
```powershell
dotnet run --project CloudPan.CodeGen
git diff --stat "**/Generated/"
# 如有意外差异 → 修复 shared-spec.json 或生成器 → 重新生成 → 重跑校验
```

### 1.2 契约版本号检查

```powershell
# 验证版本号严格相等：spec == csproj == git tag
$specVer = (Get-Content shared-spec.json | ConvertFrom-Json).version
$csprojVer = (Select-String -Path CloudPan.Client/CloudPan.Client.csproj -Pattern '<Version>(.*)</Version>').Matches.Groups[1].Value
$gitTag = git describe --tags --abbrev=0 2>$null
$gitTag = $gitTag -replace '^v', ''

Write-Host "shared-spec.json:  $specVer"
Write-Host "Client.csproj:     $csprojVer"
Write-Host "Git Tag:           $gitTag"

# 严格相等 —— 发布时三者必须完全一致
if ($specVer -ne $csprojVer) {
    Write-Host "❌ 阻断：spec 版本 ($specVer) ≠ csproj 版本 ($csprojVer)"
    exit 1
}
if ($specVer -ne $gitTag) {
    Write-Host "❌ 阻断：spec 版本 ($specVer) ≠ Git Tag ($gitTag)"
    Write-Host "   提示：发布前先 git tag -a v$specVer"
    exit 1
}
Write-Host "✅ 版本号一致: $specVer"
```

### 1.3 生成文件头校验

```powershell
$missingHeader = Get-ChildItem -Recurse -Filter "*.g.cs" |
    Where-Object { $_.FullName -notmatch '\\obj\\' -and $_.FullName -notmatch '\\bin\\' } |
    Where-Object {
        $firstLine = (Get-Content $_.FullName -First 1)
        $firstLine -notmatch 'AUTO-GENERATED from shared-spec.json'
    }
if ($missingHeader) {
    Write-Host "❌ 阻断：以下生成文件缺少 AUTO-GENERATED 头部："
    $missingHeader | ForEach-Object { Write-Host "   $_" }
    exit 1
}
Write-Host "✅ 所有生成文件有正确头部"
```

---

## Stage 2: 静态分析

**目标**: 所有自定义 Analyzer 诊断级别为 error 的规则零告警 + 补充扫描无遗漏。

### 2.1 Analyzer 诊断（Release 编译）

```powershell
dotnet build CloudPan.sln -c Release --no-restore /warnaserror 2>&1 | Tee-Object -FilePath build-diag.log
if ($LASTEXITCODE -ne 0) {
    Write-Host "❌ 阻断：Release 编译失败（存在 warning/error）"
    exit 1
}
```

| 检查项 | 通过标准 | 阻断 |
|--------|---------|------|
| 编译结果 | `Build succeeded.` | 🔴 |
| Warning 数量 | `0 warnings` | 🔴 |
| Error 数量 | `0 errors` | 🔴 |

### 2.2 Analyzer 规则逐条验收

| 分析器 | 规则 | 验证方式 | 阻断 |
|--------|------|---------|------|
| `AsyncTimerAnalyzer` | `System.Threading.Timer` 禁止同步回调 | 编译时诊断 | 🔴 |
| `DisposableResourceAnalyzer` | `IDisposable` 必须 using/Dispose | 编译时诊断 | 🔴 |
| `EndpointAuthAnalyzer` | API 端点路径 ↔ `SpecEndpoints.All` 认证模式一致 | 编译时诊断 | 🔴 |
| `EndpointRegistrationAnalyzer` | `MapGet/MapPost` 路径登记在 `SpecEndpoints.All` | 编译时诊断 | 🔴 |
| `ErrorChannelAnalyzer` | 错误响应必须通过 `ApiErrors.*` 工厂构造 | 编译时诊断 | 🔴 |
| `ErrorCodeLiteralAnalyzer` | 禁止手写错误码字符串字面量 | 编译时诊断 | 🔴 |
| `EventSubscriptionAnalyzer` | 事件订阅在析构时取消 | 编译时诊断 | 🔴 |
| `LambdaSubscriptionAnalyzer` | Lambda 事件订阅可取消 | 编译时诊断 | 🔴 |
| `LoopbackCheckAnalyzer` | 禁止 `localhost`/`127.0.0.1`/端口硬编码 | 编译时诊断 | 🔴 |
| `PortLiteralAnalyzer` | 端口号必须引用 `SpecPorts` | 编译时诊断 | 🔴 |
| `SensitiveWriteAnalyzer` | Token/密码禁止写入日志 | 编译时诊断 | 🔴 |

> ⚠️ **SensitiveWriteAnalyzer 盲区声明**：
> Analyzer 基于模式匹配，以下场景无法检测：
> - `base64(token)` 变换后写日志
> - `token.Substring(0,4) + "****"` 部分脱敏后写日志
> - 通过反射/动态生成的日志调用
> - 日志框架的 `{Token}` 模板参数在运行时展开
>
> 这些场景需要 Stage 9 安全审查中的人工代码审查兜底。

### 2.3 补充静态扫描（Analyzer 未覆盖）

```powershell
# ========== 1. 硬编码端口扫描 ==========
$portHits = Select-String -Path "CloudPan.Server\**\*.cs", "CloudPan.Client\**\*.cs" `
    -Pattern '"8443"|:8443|"8450"|:8450' |
    Where-Object { $_.Path -notmatch 'Generated|obj|bin|SpecPorts' }
if ($portHits) {
    Write-Host "❌ 阻断：发现硬编码端口："
    $portHits | ForEach-Object { Write-Host "   $($_.Path):$($_.LineNumber)" }
    exit 1
}

# ========== 2. Console.WriteLine 残留 ==========
# 仅扫描 Server 和 Client（CodeGen 和 Tests 是合法的）
$consoleHits = Select-String -Path "CloudPan.Server\**\*.cs", "CloudPan.Client\**\*.cs" `
    -Pattern 'Console\.(WriteLine|Error)\(' |
    Where-Object { $_.Path -notmatch 'Generated|obj|bin' }
# 过滤合法的：Server Program.cs 中 Serilog 初始化前的 ShowError / 启动错误处理
$illegal = $consoleHits | Where-Object {
    $_.Path -notmatch 'CloudPan\.Server\\Program\.cs'
}
if ($illegal) {
    Write-Host "❌ 阻断：非 Program.cs 中存在 Console.WriteLine/Error："
    $illegal | ForEach-Object { Write-Host "   $($_.Path):$($_.LineNumber)" }
    exit 1
}
Write-Host "✅ Console.WriteLine 扫描通过"
```

---

## Stage 3: 编译验证

**目标**: Debug + Release 双配置编译零错误，所有 6 个项目参与编译。

### 3.1 全量清理编译

```powershell
dotnet clean CloudPan.sln -c Debug
dotnet clean CloudPan.sln -c Release

# Debug
dotnet build CloudPan.sln -c Debug --no-restore 2>&1 | Tee-Object build-debug.log
if ($LASTEXITCODE -ne 0) { Write-Host "❌ 阻断：Debug 编译失败"; exit 1 }

# Release（warnings as errors）
dotnet build CloudPan.sln -c Release --no-restore /warnaserror 2>&1 | Tee-Object build-release.log
if ($LASTEXITCODE -ne 0) { Write-Host "❌ 阻断：Release 编译失败"; exit 1 }
Write-Host "✅ Debug + Release 编译通过"
```

### 3.2 项目编译矩阵

| 项目 | TFM | OutputType | Release W=Err |
|------|-----|------------|:---:|
| CloudPan.Shared | net8.0-windows | Library | ✅ |
| CloudPan.Server | net8.0-windows | WinExe | ✅ |
| CloudPan.Client | net8.0-windows | WinExe | ✅ |
| CloudPan.CodeGen | net8.0 | Exe | ✅ |
| CloudPan.Analyzers | netstandard2.0 | Library | ✅ |
| CloudPan.Tests | net8.0-windows | Library | ✅ |

### 3.3 确定性构建

> ⚠️ **重要**：PE 二进制含编译时间戳和 MVID（Module Version ID），
> 两次编译的 dll/exe 字节级不可能完全一致。
> 因此改用 **SHA-256 比较源文件 + 元数据一致性** 方法。

```powershell
# 确定性构建：比较两次编译产出的程序集元数据
# 1. 第一次编译
dotnet build CloudPan.sln -c Release
Copy-Item -Recurse CloudPan.Client/bin/Release/net8.0-windows/ $env:TEMP/build-pass1/

# 2. 清理 + 第二次编译
dotnet clean CloudPan.sln -c Release
dotnet build CloudPan.sln -c Release

# 3. 比较：文件清单一致、IL 代码一致（忽略 PE 头差异）
$diff = Compare-Object `
    (Get-ChildItem $env:TEMP/build-pass1/ -Recurse -File | Select-Object -ExpandProperty Name | Sort-Object) `
    (Get-ChildItem CloudPan.Client/bin/Release/net8.0-windows/ -Recurse -File | Select-Object -ExpandProperty Name | Sort-Object)
if ($diff) {
    Write-Host "❌ 阻断：确定性构建失败（产出文件清单不一致）"
    exit 1
}
# 4. .pdb 文件数量一致（确保调试符号生成稳定）
$pdb1 = (Get-ChildItem $env:TEMP/build-pass1/ -Recurse -Filter "*.pdb").Count
$pdb2 = (Get-ChildItem CloudPan.Client/bin/Release/net8.0-windows/ -Recurse -Filter "*.pdb").Count
if ($pdb1 -ne $pdb2) {
    Write-Host "❌ 阻断：两次编译 .pdb 数量不一致 ($pdb1 vs $pdb2)"
    exit 1
}
Write-Host "✅ 确定性构建验证通过（文件清单 + PDB 数量一致）"
```

---

## Stage 4: 单元测试

**目标**: 所有测试通过，跳过的测试有文档化理由。

### 4.1 全量测试执行

```powershell
dotnet test CloudPan.sln -c Release --no-build --verbosity normal 2>&1 | Tee-Object test-results.log
if ($LASTEXITCODE -ne 0) {
    Write-Host "❌ 阻断：单元测试存在失败"
    exit 1
}
```

| 指标 | 通过标准 | 阻断 |
|------|---------|------|
| 失败 | `Failed: 0` | 🔴 |
| 跳过 | 如有 Skip，每个必须附带代码注释 + 发布文档中说明原因 | 🟡 |
| 总计 | 测试总数 ≥ 上次发布，减少需说明 | 🟡 |

### 4.2 Skip 审查

```powershell
# 提取所有 Skipped 测试
$skipLog = Select-String -Path test-results.log -Pattern 'Skipped\s+.*\s+'
if ($skipLog) {
    Write-Host "⚠️  以下测试被跳过，需确认每个都有文档化理由："
    $skipLog | ForEach-Object { Write-Host "   $_" }
}
```

| Skip 原因 | 可接受？ | 要求 |
|-----------|---------|------|
| `[Fact(Skip = "需要外部硬件")]` | ✅ | 注释说明具体硬件要求 |
| `[Fact(Skip = "仅 Linux")]` | ✅ | 注释说明平台依赖 |
| `[Fact(Skip = "TODO: 待实现")]` | ❌ | v1.0 发布不允许 |

### 4.3 测试文件清单

#### 架构测试 (`Architecture/`)

| 测试方法 | 验证内容 |
|---------|---------|
| `Server源码_不含手写错误码字面量` | 源码扫描：无裸错误码字符串 |
| `Server源码_不含手写JSON错误体` | 源码扫描：无手写 `{"error":...}` JSON |
| `所有非UI非生成文件_小于行数上限` | 文件行数 ≤ 阈值 |
| `所有public类型_有XML文档注释` | 所有 public 类型有 `///` 注释 |
| `ErrorResponse_序列化_包含三个必需字段` | JSON 含 code/message/friendlyMessage |
| `ErrorResponse_带Detail_序列化包含detail字段` | JSON 含 detail |
| `HttpErrorCode_所有成员_在ApiErrors中有对应工厂方法` | 100% 错误码有工厂方法 |
| `SpecEndpoints_包含期望的关键端点` | 核心端点注册完整 |

#### 客户端 (`Client/`)

| 测试文件 | 关键验证点 |
|---------|-----------|
| `FileWatcherServiceTests.cs` | 文件系统变更检测正确性、Buffer overflow 兜底 |
| `SyncEngineTests.cs` | 同步引擎状态机、冲突检测、进度报告 |

#### 服务端 (`Server/`)

| 测试文件 | 关键验证点 |
|---------|-----------|
| `FilesControllerIntegrationTests.cs` | 文件 CRUD API 端到端（WebApplicationFactory） |
| `FileIndexServiceTests.cs` | 文件索引查询/更新 |
| `FileStorageServiceTests.cs` | 原子写入、哈希校验、存储路径 |
| `TokenAuthMiddlewareTests.cs` | Token 认证、ACL 权限 |
| `VersionServiceTests.cs` | 版本号原子递增 |

#### 基准 (`Benchmarks/`)

| 测试文件 | 关键验证点 |
|---------|-----------|
| `HashBenchmarks.cs` | SHA-256 哈希性能 |
| `PathValidationBenchmarks.cs` | 路径安全检查性能 |

---

## Stage 5: 架构门禁

**目标**: 架构约束不退化，新增代码不违反既有规则。

### 5.1 项目依赖方向检查

```powershell
# 严格方向：Shared ← Server|Client（单向）
# 如果 Server 引用 Client 或 Client 引用 Server → 编译错误 → 但在此显式验证

$serverRefs = Select-String -Path CloudPan.Server/CloudPan.Server.csproj -Pattern 'ProjectReference'
$clientRefs = Select-String -Path CloudPan.Client/CloudPan.Client.csproj -Pattern 'ProjectReference'
$sharedRefs = Select-String -Path CloudPan.Shared/CloudPan.Shared.csproj -Pattern 'ProjectReference'

# Server 只能引用 Shared
if (($serverRefs -match 'CloudPan\.Client') -or ($serverRefs -match 'CloudPan\.Tests')) {
    Write-Host "❌ 阻断：Server 引用了 Client 或 Tests"
    exit 1
}
# Client 只能引用 Shared
if ($clientRefs -match 'CloudPan\.Server') {
    Write-Host "❌ 阻断：Client 引用了 Server"
    exit 1
}
# Shared 不能有项目引用
if ($sharedRefs) {
    Write-Host "❌ 阻断：Shared 不应有项目引用"
    exit 1
}
Write-Host "✅ 依赖方向正确"
```

### 5.2 命名空间一致性

```powershell
$violations = @()
foreach ($proj in @("CloudPan.Server", "CloudPan.Client", "CloudPan.Shared", "CloudPan.Tests")) {
    $prefix = $proj -replace '\.', '\.'
    $bad = Select-String -Path "$proj\**\*.cs" -Pattern '^namespace\s+(?!'"$prefix"')' |
        Where-Object { $_.Path -notmatch 'Generated|obj|bin' }
    $violations += $bad
}
if ($violations) {
    Write-Host "❌ 阻断：发现命名空间不一致："
    $violations | ForEach-Object { Write-Host "   $($_.Path):$($_.LineNumber) $($_.Line.Trim())" }
    exit 1
}
Write-Host "✅ 命名空间一致"
```

### 5.3 生成文件标记验证

```powershell
$unguarded = Get-ChildItem -Recurse -Filter "*.g.cs" |
    Where-Object { $_.FullName -notmatch '\\obj\\' -and $_.FullName -notmatch '\\bin\\' } |
    Where-Object { (Get-Content $_.FullName -First 1) -notmatch 'AUTO-GENERATED' }
if ($unguarded) {
    Write-Host "❌ 阻断：生成文件缺少 AUTO-GENERATED 头部"
    exit 1
}
Write-Host "✅ 生成文件标记完整"
```

### 5.4 文件行数门禁（v1.0 阈值）

| 类别 | 行数上限 | 阻断 |
|------|---------|------|
| 核心服务/控制器 | ≤ 1200 行 | 🟡 |
| SyncEngine.cs | ≤ 1800 行 | 🟡 |
| 其他源文件 | ≤ 600 行 | 🟡 |
| 测试文件 | 无硬上限 | — |

> v1.0 已知超标：`SyncEngine.cs`（Phase 2 拆分目标）。

---

## Stage 6: 代码覆盖率

**目标**: v1.0 正式发布覆盖率显著高于原型阶段，核心模块达标。

### 6.1 覆盖率收集

```powershell
dotnet test CloudPan.sln -c Release --no-build `
    /p:CollectCoverage=true `
    /p:CoverletOutputFormat=cobertura,json `
    /p:CoverletOutput=./TestResults/coverage/ 2>&1 | Tee-Object coverage-run.log
```

报告位置：
- Cobertura: `CloudPan.Tests/TestResults/coverage/*.cobertura.xml`
- JSON: `CloudPan.Tests/TestResults/coverage/*.json`

### 6.2 覆盖率门禁（v1.0 正式版）

| 模块 | 最低行覆盖 | 最低分支覆盖 | 阻断 | 说明 |
|------|-----------|-------------|------|------|
| CloudPan.Server/Services/ | 75% | 65% | 🔴 | 核心业务逻辑 |
| CloudPan.Server/Middleware/ | 85% | 75% | 🔴 | 请求路径必经 |
| CloudPan.Server/Controllers/ | 65% | 55% | 🔴 | 控制器层 |
| CloudPan.Client/Services/SyncEngine.cs | 60% | 50% | 🟡 | 文件系统依赖，难完全模拟 |
| CloudPan.Client/Services/FileWatcherService.cs | 55% | 45% | 🟡 | 依赖真实文件系统事件 |
| CloudPan.Shared/ | 70% | 60% | 🔴 | 共享类型 |
| **全局** | **65%** | **55%** | 🔴 | v1.0 最低门槛 |

### 6.3 覆盖率趋势

```powershell
# 对比上次发布覆盖率
# 下降 ≥ 5% → 🟡 需在 Release Notes 说明原因
# 下降 ≥ 10% → 🔴 阻断
# 新模块（首次纳入）豁免趋势对比，但必须达到 6.2 门禁
```

---

## Stage 7: 性能与稳定性测试

**目标**: 无性能回归，无内存泄漏，核心路径性能在可接受范围内。

### 7.1 微基准测试

```powershell
dotnet run -c Release --project CloudPan.Tests -- --filter "*" 2>&1 | Tee-Object benchmark-results.log
```

| 基准测试 | 指标 | v1.0 阈值 | 阻断 |
|---------|------|----------|------|
| `HashBenchmarks` | SHA-256 吞吐量 | ≥ 150 MB/s | 🔴 |
| `PathValidationBenchmarks` | 单次操作 | ≤ 5 μs | 🔴 |

**回归判断**：与上次发布基准对比，性能下降 ≥ 20% → 🔴 阻断。

### 7.2 新增基准测试要求（v1.0）

以下基准在发布前需就位：

| 新增基准 | 被测路径 | 最低要求 | 阻断 |
|---------|---------|---------|------|
| 文件树扫描 | 10000 文件目录全量扫描 | ≤ 2 秒 | 🟡 |
| SQLite 批量写入 | 1000 条 FileIndex 批量 INSERT | ≤ 500 ms | 🟡 |
| 同步引擎状态机 | 100 个变更事件端到端处理 | ≤ 1 秒 | 🟡 |
| GC 压力 | 扫描 10000 文件期间 GC 次数 | Gen2 ≤ 2 次 | 🟡 |

> 🟡 条件通过：v1.0 允许基准未就位但需在 Release Notes 中声明，
> v1.1 起升级为 🔴 阻断。

### 7.3 冷启动时间

| 进程 | v1.0 目标 | 测量方式 |
|------|----------|---------|
| 客户端 → 托盘图标 | ≤ 3 秒 | `Measure-Command { Start-Process ... -Wait }` |
| 服务端 → HTTP 就绪 | ≤ 5 秒 | 轮询 `http://localhost:8443/api/health` 直到 200 |

### 7.4 浸泡测试（内存泄漏检测）

```powershell
# 浸泡测试：客户端常驻运行 1 小时，连续触发文件变更
# Phase 0 → 手动执行
# v1.1 → 自动化（GitHub Actions 定时触发）
```

| 检查项 | v1.0 要求 | 阻断 |
|--------|----------|------|
| 1 小时内存趋势 | RSS 增长 ≤ 20%（排除 GC 波动） | 🟡 |
| 句柄泄漏 | 运行前后 Handle Count 差异 ≤ 50 | 🟡 |
| 定时器清理 | 停止所有服务后无残留 Timer 线程 | 🟡 |

```powershell
# 快速检查（代替完整浸泡测试）
$proc = Start-Process -FilePath "dotnet" -ArgumentList "run --project CloudPan.Server -c Release" -PassThru
Start-Sleep -Seconds 30
$initialMem = (Get-Process -Id $proc.Id).WorkingSet64
# ... 触发 100 次文件变更 ...
Start-Sleep -Seconds 10
$finalMem = (Get-Process -Id $proc.Id).WorkingSet64
$growth = ($finalMem - $initialMem) / $initialMem * 100
if ($growth -gt 50) {
    Write-Host "❌ 阻断：30 秒测试内存增长 ${growth:N1}%"
    exit 1
}
Write-Host "✅ 快速内存测试通过（增长 ${growth:N1}%）"
Stop-Process -Id $proc.Id -Force
```

---

## Stage 8: 集成测试

**目标**: 核心业务流程和异常路径有自动化覆盖，手动验证仅作补充。

### 8.1 强制自动化场景

以下场景 **必须** 使用 `WebApplicationFactory<Program>` + 模拟客户端实现自动化：

| 场景 | 自动化测试要求 | 阻断 |
|------|--------------|------|
| 文件上传（小文件 < 1MB） | 必须自动化 | 🔴 |
| 文件下载 | 必须自动化 | 🔴 |
| 文件删除同步 | 必须自动化 | 🔴 |
| Token 认证 → 401 响应 | 必须自动化 | 🔴 |
| WebSocket 首条消息认证 | 必须自动化 | 🔴 |
| 版本号递增 | 必须自动化（已覆盖：VersionServiceTests） | 🔴 |
| 冲突检测 | 必须自动化 | 🔴 |

### 8.2 自动化边界条件

| 场景 | 自动化策略 | v1.0 要求 |
|------|-----------|----------|
| 服务端宕机 → 客户端检测 | Mock WebSocket 异常 → 验证重连逻辑 | 🔴 |
| Token 错误 → 401 | `WebApplicationFactory` 发错误 Token | 🔴 |
| 空文件同步 | 上传 0 字节文件 → 验证哈希一致 | 🔴 |
| 文件名含 Unicode/Emoji | 上传 `测试📁.txt` → 验证 | 🔴 |
| 大小写冲突（NTFS） | 创建 `ReadMe.txt` 和 `README.txt` → 验证行为 | 🔴 |

### 8.3 手动补充场景（每次发布前执行）

以下场景因依赖真实网络/文件系统，自动化成本高，v1.0 允许手动：

| 场景 | 操作 | 预期行为 | 阻断 |
|------|------|---------|------|
| 大文件传输 | 100MB 文件上传→下载，比较 SHA-256 | 哈希一致 | 🟡 |
| 客户端断网恢复 | 拔网线 → 恢复 → 变更同步 | 恢复后全量扫描同步 | 🟡 |
| 磁盘满 | 填满磁盘 → 尝试下载 | .tmp 无法 rename → 不损坏已有文件 | 🟡 |
| 文件锁定（Excel打开中） | 打开文件 → 尝试同步 | 跳过/重试 → 不崩溃 | 🟡 |
| 并发多客户端 | 3 个客户端同时上传不同文件 | 各自独立处理 | 🟡 |

### 8.4 文件系统边界条件

| 场景 | 操作 | 预期行为 | 阻断 |
|------|------|---------|------|
| NTFS 替代数据流（ADS） | 下载文件含 Zone.Identifier | 不传播 ADS 标记 | 🟡 |
| 符号链接/Junction | 同步根中有指向 `C:\Windows` 的 Junction | 跳过符号链接 → 日志警告 | 🟡 |
| UTF-8/Emoji 文件名 | `日本語テスト📂.txt` | 正确同步，文件名不损坏 | 🟡 |
| 大小写敏感 | `ReadMe.txt` vs `readme.txt` | 检测冲突，不静默覆盖 | 🟡 |
| FSW Buffer Overflow | 批量创建 5000+ 文件 | 5 分钟兜底扫描捕获所有遗漏 | 🟡 |
| 路径 > 260 字符 | 深层嵌套目录 | 跳过 → 日志警告 | 🟡 |
| 非法 Windows 字符 | `test:file?.txt` | 过滤/跳过 → 日志 | 🟡 |

### 8.5 UDP 局域网发现测试

```powershell
# UDP 发现需要至少 2 台设备或本地回环测试
```

| 场景 | 操作 | 预期行为 | 阻断 |
|------|------|---------|------|
| 广播发送 | 启动服务端 → 发送 UDP 广播 | 客户端收到广播中的 server URL | 🟡 |
| 端口冲突 | 另一进程占用 `udpDiscoveryPort` (8450) | 服务端检测冲突 → 日志警告 → 降级运行 | 🟡 |
| 多网卡 | 2+ 网卡设备 | 所有网卡均发送广播 | 🟡 |
| 防火墙拦截 | Windows 防火墙默认阻止 | 首次运行提示或自动添加规则 | 🟡 |

### 8.6 WebSocket 生命周期测试

| 场景 | 自动化/手动 | 验证点 |
|------|-----------|--------|
| 正常连接→心跳→断开 | 自动化 | Ping/Pong 间隔符合 SpecConfig |
| 心跳超时→客户端重连 | 自动化 | 重连期间积压的变更不丢失 |
| 服务端主动关闭 | 自动化 | 客户端清理资源 → 延迟重连 |
| 消息乱序 | 手动 | 客户端按 Seq 号排序（如有） |

### 8.7 数据库升级测试

```powershell
# 模拟 v0.9 → v1.0 数据库升级
# 1. 检出旧版本 → 启动服务端 → 创建测试数据 → 停止
# 2. 检出新版本 → 启动服务端 → 验证
```

| 检查项 | 验证方式 | 阻断 |
|--------|---------|------|
| `EnsureCreated()` 对已有数据库的行为 | 旧 DB + 新代码启动 | 🔴 |
| 新增列（如 `TargetPath`）的默认值 | 旧记录查询不报错 | 🔴 |
| 索引变更不破坏查询 | 关键查询 EXPLAIN QUERY PLAN | 🟡 |
| `.cloudpan` 目录结构兼容 | 旧版元数据可被新版读取 | 🔴 |

> 如果有破坏性 schema 变更 → 必须提供迁移脚本 + 文档。

---

## Stage 9: 安全审查

**目标**: 无已知高危漏洞，Token/密钥/路径不泄露，隐私数据受保护。

### 9.1 Secret 泄露扫描

```powershell
$secretHits = Select-String -Path "CloudPan.Server\**\*.cs", "CloudPan.Client\**\*.cs" `
    -Pattern '(token|password|secret|apikey|key)\s*=\s*"[^"]{8,}"' `
    -CaseSensitive:$false |
    Where-Object { $_.Path -notmatch 'Generated|obj|bin|SpecPorts|example|placeholder|test|mock|xxx' }
if ($secretHits) {
    Write-Host "❌ 阻断：发现疑似硬编码凭据："
    $secretHits | ForEach-Object { Write-Host "   $($_.Path):$($_.LineNumber)" }
    exit 1
}
Write-Host "✅ Secret 扫描通过"
```

### 9.2 配置文件凭据检查

```powershell
Get-ChildItem -Recurse -Include "appsettings*.json" |
    ForEach-Object {
        $content = Get-Content $_.FullName -Raw
        if ($content -match '"(token|password|secret)"\s*:\s*"(?!\s*$|(your-|CHANGE_ME|PLACEHOLDER))[^"]{4,}"') {
            Write-Host "❌ 阻断：$($_.FullName) 包含真实凭据"
            exit 1
        }
    }
Write-Host "✅ 配置文件凭据检查通过"
```

### 9.3 依赖项安全审计

```powershell
dotnet list CloudPan.sln package --vulnerable 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Host "⚠️  dotnet list package --vulnerable 未正确执行（.NET SDK 版本可能不支持）"
}

# NuGet 漏洞审计（需要安装 dotnet-retire 或手动审查）
# dotnet retire --path CloudPan.sln
```

| 漏洞级别 | 动作 | 阻断 |
|---------|------|------|
| Critical / High | 立即升级依赖 | 🔴 |
| Medium | 评估可利用性 → 修复或文档化豁免 | 🔴 |
| Low | 记录在 Release Notes | 🟡 |

### 9.4 敏感路径访问控制

```powershell
# .cloudpan 目录不能通过 HTTP 直接暴露
$exposedCloudpan = Select-String -Path "CloudPan.Server\Controllers\**\*.cs" `
    -Pattern '\.cloudpan' |
    Where-Object { $_ -notmatch '^\s*//' }
if ($exposedCloudpan) {
    Write-Host "⚠️  Controller 中引用了 .cloudpan 路径："
    $exposedCloudpan | ForEach-Object { Write-Host "   $($_.Path):$($_.LineNumber)" }
    Write-Host "   验证这些引用是否通过 Service 层间接访问（非直接暴露）"
}
```

### 9.5 Token 安全（9 项检查）

| # | 检查项 | 验证方式 | 阻断 |
|---|--------|---------|------|
| 1 | Token 生成 | `RandomNumberGenerator`，长度 ≥ 32 字符 | 🔴 |
| 2 | Token 传输 | WebSocket 首条消息体中（非 URL query） | 🔴 |
| 3 | Token 日志安全 | SensitiveWriteAnalyzer 零告警 | 🔴 |
| 4 | Token 存储权限 | `client-config.json` 仅当前用户可读 | 🟡 |
| 5 | Token 无 HTTP Header 泄露 | 响应头中不含 Token | 🔴 |
| 6 | Token 超时 | 支持 Token 过期机制 | 🟡 |
| 7 | Token 撤销 | 支持服务端撤销 Token | 🟡 |
| 8 | Token 变换后日志 | 人工审查：无 `base64(token)` 等变换后写日志 | 🔴 |
| 9 | Token 子串日志 | 人工审查：无 `token.Substring(0,4)+"****"` 等伪脱敏写日志 | 🔴 |

### 9.6 隐私与数据泄露

| 检查项 | 验证方式 | 阻断 |
|--------|---------|------|
| 错误响应路径脱敏 | `detail` 字段不含 `C:\Users\xxx\...` 绝对路径 | 🔴 |
| 日志路径脱敏 | 日志中文件路径不含用户名（或用户已明确同意） | 🟡 |
| Swagger 公开 | v1.0 生产环境 Swagger 应禁用或仅 localhost 可访问 | 🟡 |
| 服务端版本在响应头 | `Server` 响应头不暴露具体版本号 | 🟡 |

### 9.7 网络传输安全（v1.0 状态声明）

> ⚠️ **v1.0 已知限制**：未启用 TLS。HTTP 明文传输在家庭局域网内可接受，
> 但必须在 Release Notes 和用户文档中明确告知用户。
> v1.1 计划引入 TLS + 自签名证书 + 指纹 pinning。

---

## Stage 10: 打包与发布

**目标**: 产出可分发的发布包，版本号正确，许可合规。

### 10.1 版本号一致性

> 已在 Stage 1.2 完成。此处为冗余检查。

```powershell
$specVer = (Get-Content shared-spec.json | ConvertFrom-Json).version
$gitTag = (git describe --tags --abbrev=0 2>$null) -replace '^v', ''
if ($specVer -ne $gitTag) {
    Write-Host "❌ 阻断：spec 版本 ($specVer) ≠ Git Tag ($gitTag)"
    Write-Host "   执行: git tag -a v$specVer -m 'Release v$specVer'"
    exit 1
}
```

### 10.2 Release 发布

```powershell
# 目标架构矩阵
$rids = @("win-x64", "win-arm64")

foreach ($rid in $rids) {
    Write-Host "--- 发布 $rid ---"

    # 服务端
    dotnet publish CloudPan.Server -c Release `
        -o "publish/$rid/server/" `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:RuntimeIdentifier=$rid
    if ($LASTEXITCODE -ne 0) { Write-Host "❌ Server $rid 发布失败"; exit 1 }

    # 客户端
    dotnet publish CloudPan.Client -c Release `
        -o "publish/$rid/client/" `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:RuntimeIdentifier=$rid
    if ($LASTEXITCODE -ne 0) { Write-Host "❌ Client $rid 发布失败"; exit 1 }
}
```

| 检查项 | 通过标准 | 阻断 |
|--------|---------|------|
| 发布退出码 | 0 | 🔴 |
| exe 存在 | `publish/win-x64/server/CloudPan.Server.exe` | 🔴 |
| exe 可执行 | 双击启动无弹框报错 | 🔴 |
| 文件大小 | Server.exe ≤ 80 MB，Client.exe ≤ 80 MB | 🟡 |
| 文件大小（arm64） | 同上 | 🟡 |
| `appsettings.json` 含默认值 | 端口 8443，无真实凭据 | 🔴 |

### 10.3 发布包结构

```
publish/
├── win-x64/
│   ├── server/
│   │   ├── CloudPan.Server.exe
│   │   ├── appsettings.json          # 默认配置
│   │   └── ...
│   └── client/
│       ├── CloudPan.Client.exe
│       └── ...
├── win-arm64/
│   ├── server/
│   └── client/
├── README.txt                        # 安装与使用说明
└── THIRD-PARTY-NOTICES.txt           # 第三方许可声明
```

### 10.4 依赖许可合规

```powershell
# 列出所有第三方 NuGet 包及许可证
dotnet list CloudPan.Client package --include-transitive 2>&1 | Tee-Object nuget-packages.log
dotnet list CloudPan.Server package --include-transitive 2>&1 | Tee-Object -Append nuget-packages.log

Write-Host "⚠️  手动验证项："
Write-Host "   1. 所有第三方包的 license 与项目兼容（MIT/Apache-2.0/BSD → OK; GPL → 需评估）"
Write-Host "   2. self-contained 发布含 .NET Runtime → 需包含微软的第三方声明"
Write-Host "   3. 产出 THIRD-PARTY-NOTICES.txt 随发布包分发"
```

| 许可类型 | 兼容性 | 要求 |
|---------|--------|------|
| MIT, Apache-2.0, BSD, ISC | ✅ 兼容 | 保留版权声明 |
| GPL, LGPL, AGPL | ⚠️ 评估 | 法律审查确认无 copyleft 传染 |
| 未知/无许可证 | ❌ 阻断 | 联系作者或移除依赖 |

### 10.5 回滚计划验证

```powershell
# 每次发布前验证：安装旧版本 → 升级到新版本 → 回滚到旧版本 → 数据不损坏
```

| 检查项 | 验证方式 | 阻断 |
|--------|---------|------|
| 数据库向下兼容 | 新版创建的 DB 能否被旧版代码打开 | 🟡 |
| `.cloudpan` 目录兼容 | 旧版能否识别新版的元数据格式 | 🟡 |
| 回滚后文件不丢失 | 新版同步的文件在回滚后仍可访问 | 🔴 |
| 配置文件格式 | 旧版能否解析新版写的 `client-config.json` | 🟡 |

> 如果有破坏性变更（数据库 schema / 元数据格式 / 配置文件格式不兼容）：
> 必须提供迁移脚本 + 回滚脚本 + 用户文档。

---

## Stage 11: 最终发布清单

### 11.1 全阶段勾选

- [ ] Stage 0:  工作区清洁 — `git status --porcelain` 空
- [ ] Stage 1:  契约一致性 — `--verify` 通过，版本号三者严格相等
- [ ] Stage 2:  静态分析 — 11 Analyzer 零告警 + 补充扫描零命中
- [ ] Stage 3:  编译 — Debug + Release 双通过，确定性构建一致
- [ ] Stage 4:  单元测试 — 全部通过，Skip 有文档化理由
- [ ] Stage 5:  架构门禁 — 依赖方向/命名空间/生成文件标记正确
- [ ] Stage 6:  覆盖率 — 全局 ≥ 65% 行覆盖，核心模块达标
- [ ] Stage 7:  性能测试 — 微基准无回归，快速内存测试通过
- [ ] Stage 8:  集成测试 — 7 项自动化场景通过 + 手动边界条件全部执行
- [ ] Stage 9:  安全审查 — 零高危漏洞、零 Token 泄露、路径脱敏
- [ ] Stage 10: 打包 — 产出 win-x64 + win-arm64，许可合规
- [ ] `shared-spec.json` version 已更新
- [ ] `CHANGELOG.md` 已记录本版本变更
- [ ] Git tag 已创建并推送
- [ ] Release Notes 已写（含已知限制、豁免项、回滚指南）

### 11.2 Git 操作

```powershell
$version = (Get-Content shared-spec.json | ConvertFrom-Json).version
git tag -a "v$version" -m "Release v$version"
git push origin "v$version"
git push origin main
```

### 11.3 验收决策矩阵

| 阻断级别 | 条件 | 动作 |
|---------|------|------|
| 🔴 阻断 | 任一带 🔴 标记的项失败 | 禁止发布，修复后重跑全流程 |
| 🟡 条件 | 仅有 🟡 标记项未达标 | Release Notes 披露 + 记录豁免原因 |
| 🟢 通过 | 全部绿灯 | 正常发布 |

### 11.4 本方案能挡住什么

| Bug 类型 | 能否拦住 | 依赖阶段 |
|----------|---------|---------|
| 契约与代码不一致 | ✅ | Stage 1 |
| 编译/类型错误 | ✅ | Stage 2, 3 |
| Analyzer 违反 | ✅ | Stage 2 |
| 未提交变更混入发布 | ✅ | Stage 0 |
| 核心逻辑错误 | ✅ | Stage 4, 8 |
| 同步引擎静默丢文件 | ✅ | Stage 8 自动化集成测试 |
| 数据库升级损坏 | ✅ | Stage 8.7 |
| 安全漏洞（Token/路径泄露） | ✅ | Stage 9 |
| 内存泄漏（短期） | ⚠️ | Stage 7.4 快速检查 |
| 内存泄漏（长期浸泡） | ❌ | v1.1 自动化 |
| 网络异常恢复完整性 | ⚠️ | Stage 8.3 手动为主 |
| 极端文件系统边界（ADS/Junction） | ⚠️ | Stage 8.4 手动 |
| 依赖许可证传染 | ⚠️ | Stage 10.4 人工审查 |

---

## 附录 A: 手动集成测试检查表

```
发布版本: ________    测试日期: ________    测试人: ________

=== 自动化场景（dotnet test --filter "IntegrationTests"） ===
[ ] 文件上传（小文件）        [ ] 通过  [ ] 失败  _________
[ ] 文件下载                  [ ] 通过  [ ] 失败  _________
[ ] 文件删除同步              [ ] 通过  [ ] 失败  _________
[ ] Token 认证 → 401          [ ] 通过  [ ] 失败  _________
[ ] WebSocket 首条消息认证    [ ] 通过  [ ] 失败  _________
[ ] 冲突检测                  [ ] 通过  [ ] 失败  _________
[ ] Unicode 文件名            [ ] 通过  [ ] 失败  _________

=== 手动场景 ===
[ ] 大文件传输 100MB          [ ] 通过  [ ] 失败  _________
[ ] 客户端断网恢复            [ ] 通过  [ ] 失败  _________
[ ] 磁盘满保护                [ ] 通过  [ ] 失败  _________
[ ] 文件锁定（Excel打开）     [ ] 通过  [ ] 失败  _________
[ ] 并发 3 客户端             [ ] 通过  [ ] 失败  _________
[ ] NTFS ADS 不传播           [ ] 通过  [ ] 失败  _________
[ ] 符号链接跳过              [ ] 通过  [ ] 失败  _________
[ ] 大小写冲突检测            [ ] 通过  [ ] 失败  _________
[ ] FSW Buffer Overflow       [ ] 通过  [ ] 失败  _________
[ ] UDP 广播发现              [ ] 通过  [ ] 失败  _________
[ ] UDP 端口冲突降级          [ ] 通过  [ ] 失败  _________

=== 数据库升级 ===
[ ] 旧 DB + 新代码启动       [ ] 通过  [ ] 失败  _________
[ ] 新增列默认值正常           [ ] 通过  [ ] 失败  _________
[ ] 回滚后数据不丢失           [ ] 通过  [ ] 失败  _________

备注: _____________________________________________
```

## 附录 B: 一键验收脚本（PowerShell）

```powershell
# cloudpan-release-check.ps1
# 置于解决方案根目录运行
# 用法: .\cloudpan-release-check.ps1
param(
    [switch]$SkipBenchmarks,
    [switch]$SkipCoverage,
    [switch]$SkipPublish
)

$ErrorActionPreference = "Stop"
$script:Pass = 0
$script:Fail = 0

function Check($Stage, [ScriptBlock]$Script) {
    Write-Host -NoNewline "[....] $Stage ... "
    try {
        & $Script
        Write-Host "`r[PASS] $Stage" -ForegroundColor Green
        $script:Pass++
    } catch {
        Write-Host "`r[FAIL] $Stage : $_" -ForegroundColor Red
        $script:Fail++
    }
}

function Warn($Message) {
    Write-Host "[WARN] $Message" -ForegroundColor Yellow
}

Write-Host "==========================================" -ForegroundColor Cyan
Write-Host " CloudPan 发布验收脚本 v2.0" -ForegroundColor Cyan
Write-Host " $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')" -ForegroundColor Cyan
Write-Host "==========================================" -ForegroundColor Cyan
Write-Host ""

# ========== Stage 0: 工作区清洁 ==========
Check "Stage 0.1: Git 工作区清洁" {
    $porcelain = git status --porcelain 2>&1
    if ($porcelain) { throw "工作区不清洁: $porcelain" }
}
Check "Stage 0.2: 当前分支为 main" {
    $branch = git branch --show-current
    if ($branch -ne "main") { throw "当前分支: $branch，应在 main 发布" }
}

# ========== Stage 1: 契约一致性 ==========
Check "Stage 1.1: 代码生成器校验" {
    dotnet run --project CloudPan.CodeGen -- --verify
    if ($LASTEXITCODE -ne 0) { throw "退出码: $LASTEXITCODE" }
}
Check "Stage 1.2: 版本号一致性" {
    $specVer = (Get-Content shared-spec.json -Raw | ConvertFrom-Json).version
    $csprojMatch = Select-String -Path CloudPan.Client/CloudPan.Client.csproj -Pattern '<Version>(.*)</Version>'
    $csprojVer = $csprojMatch.Matches.Groups[1].Value
    $gitTag = (git describe --tags --abbrev=0 2>$null) -replace '^v', ''
    if ($specVer -ne $csprojVer) { throw "spec($specVer) != csproj($csprojVer)" }
    if ($specVer -ne $gitTag) { throw "spec($specVer) != tag($gitTag) — 请先打 tag" }
}

# ========== Stage 2+3: 编译 + 静态分析 ==========
Check "Stage 2+3: Release 编译 (warn=error)" {
    dotnet build CloudPan.sln -c Release /warnaserror --no-restore 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "编译失败" }
}
Check "Stage 3: Debug 编译" {
    dotnet build CloudPan.sln -c Debug --no-restore 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "编译失败" }
}

# ========== Stage 4: 单元测试 ==========
Check "Stage 4: 单元测试" {
    $output = dotnet test CloudPan.sln -c Release --no-build --verbosity normal 2>&1
    if ($LASTEXITCODE -ne 0) { throw "存在测试失败" }
    Write-Host $output
    # 非阻断：Skip 审查
    if ($output -match 'Skipped') {
        Warn "存在 Skipped 测试，请确认每个有文档化理由"
    }
}

# ========== Stage 6: 覆盖率（可选跳过） ==========
if (-not $SkipCoverage) {
    Check "Stage 6: 代码覆盖率收集" {
        dotnet test CloudPan.sln -c Release --no-build `
            /p:CollectCoverage=true `
            /p:CoverletOutputFormat=json `
            /p:CoverletOutput=./TestResults/coverage/ 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "覆盖率收集失败" }
    }
} else {
    Warn "跳过覆盖率收集"
}

# ========== Stage 7: 基准测试（可选跳过） ==========
if (-not $SkipBenchmarks) {
    Check "Stage 7: 基准测试" {
        dotnet run -c Release --project CloudPan.Tests -- --filter "*" 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "基准测试失败" }
    }
} else {
    Warn "跳过基准测试"
}

# ========== Stage 9: 安全扫描 ==========
Check "Stage 9.1: Secret 泄露扫描" {
    $hits = Select-String -Path "CloudPan.Server\**\*.cs", "CloudPan.Client\**\*.cs" `
        -Pattern '(token|password|secret|apikey|key)\s*=\s*"[^"]{8,}"' -CaseSensitive:$false |
        Where-Object { $_.Path -notmatch 'Generated|obj|bin|SpecPorts|example|placeholder|test|mock|xxx' }
    if ($hits) {
        $hits | ForEach-Object { Write-Host "   $($_.Path):$($_.LineNumber)" }
        throw "发现疑似硬编码凭据"
    }
}
Check "Stage 9.3: 依赖项安全审计" {
    $vuln = dotnet list CloudPan.sln package --vulnerable 2>&1
    if ($vuln -match 'Critical|High') {
        Warn "存在高危漏洞依赖，请升级"
        # 🟡 条件通过，不阻断
    }
}

# ========== Stage 10: 打包（可选跳过） ==========
if (-not $SkipPublish) {
    Check "Stage 10: win-x64 发布" {
        dotnet publish CloudPan.Server -c Release -o publish/win-x64/server/ `
            --self-contained true -p:PublishSingleFile=true -p:RuntimeIdentifier=win-x64 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Server 发布失败" }
        dotnet publish CloudPan.Client -c Release -o publish/win-x64/client/ `
            --self-contained true -p:PublishSingleFile=true -p:RuntimeIdentifier=win-x64 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Client 发布失败" }
        if (-not (Test-Path publish/win-x64/server/CloudPan.Server.exe)) { throw "Server.exe 不存在" }
        if (-not (Test-Path publish/win-x64/client/CloudPan.Client.exe)) { throw "Client.exe 不存在" }
    }
} else {
    Warn "跳过发布打包"
}

# ========== 结果汇总 ==========
Write-Host ""
Write-Host "==========================================" -ForegroundColor Cyan
Write-Host "  结果: $script:Pass 通过 / $script:Fail 失败" -ForegroundColor Cyan
Write-Host "==========================================" -ForegroundColor Cyan

if ($script:Fail -gt 0) {
    Write-Host "发布阻断: $script:Fail 项失败" -ForegroundColor Red
    Write-Host "修复后重新运行: .\cloudpan-release-check.ps1" -ForegroundColor Yellow
    exit 1
} else {
    Write-Host "全部通过，可以发布" -ForegroundColor Green
    Write-Host "别忘了完成手动检查项（附录 A）和最终清单（Stage 11）" -ForegroundColor Yellow
    exit 0
}
```

---

> **审查状态**: ⏳ 待用户审查 v2.0
>
> **v2.0 核心变更摘要**：
> - 🔴 新增 Stage 0（工作区清洁）— 防止 dirty working tree 发布
> - 🔴 版本号从 `≥` 改为严格相等
> - 🔴 全局覆盖率从 50% 上调至 65%，核心模块分别上调
> - 🔴 集成测试从手动为主改为自动化为主（7 项强制自动化）
> - 🔴 确定性构建改用清单+PDB 比较（修复 PE 时间戳导致的原方案无效）
> - 🔴 Skip 从 0 容忍改为文档化审查
> - 🔴 Console.WriteLine 扫描改为仅检查 Server+Client（非 CodeGen/Tests）
> - 🔴 新增数据库升级测试、UDP 发现、WebSocket 生命周期、文件系统 7 类边界条件
> - 🔴 新增隐私/路径脱敏、依赖许可证、回滚计划验证
> - 🔴 脚本从 Bash 改为 PowerShell（Windows 原生运行，不依赖 jq）
> - 🔴 发布目标从 win-x64 扩展到 win-x64 + win-arm64
