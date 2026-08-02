# CloudPan 端到端审查方案

> **版本**: v1.0
> **日期**: 2026-08-02
> **适用**: v1.0.0 正式发布版及后续版本
> **状态**: ✅ 已批准（配套套件 `e2e-test.sh` 可执行）
> **配套**: [../e2e-test.sh](../e2e-test.sh)（真实进程 E2E 套件）、`docs/e2e-review-report-20260802.md`（审查报告模板/样例）

---

## 1. 标准依据与术语

本方案按以下国际标准组织，术语遵循各标准定义：

| 标准 | 用途 |
|------|------|
| **ISO/IEC/IEEE 29119**（软件测试） | 测试过程（Part 2）、测试文档（Part 3）、测试技术（Part 4）；测试级别：组件 → 集成 → 系统 → 验收 |
| **ISO/IEC 25010**（质量模型） | 8 类质量特性 → 场景矩阵分组依据：功能适合性 / 性能效率 / 兼容性 / 易用性 / 可靠性 / 安全性 / 可维护性 / 可移植性 |
| **IEEE 829**（测试文档标准） | 测试计划、测试日志、测试事件报告、测试汇总报告的模板依据 |
| **ISTQB**（测试认证） | 测试过程：计划 → 分析与设计 → 实施 → 执行 → 完成 |
| **OWASP ASVS**（应用安全验证标准） | 安全场景的验证级别与覆盖范围（认证、访问控制、输入验证、限流） |
| **测试金字塔 / 风险驱动测试（ISO 31000）** | 测试层级投入分配；按"缺陷影响面 × 发生概率"排序测试优先级 |

**关键术语**：
- **端到端（E2E）审查**：启动真实服务端 + 客户端进程，以真实文件/网络/数据库链路验证业务场景，而非 mock 或内存管道。
- **场景（Scenario）**：一组可重复执行的操作步骤 + 可观测断言 + 验收标准。
- **阻断级别**：🔴 阻断（不通过即视为"不能正常使用"）/ 🟡 条件（需文档化披露）/ 🟢 通过。

## 2. 审查目标与范围

**目标**：证明 v1.0.0 产品在真实运行环境下**可以正常使用**——核心业务闭环（文件同步、版本、分享、回收站）正确，安全防护有效，异常恢复不丢数据。

**范围**（v1.0 功能清单 × 质量特性矩阵）：

| v1.0 功能 | 功能 | 安全 | 可靠性 | 兼容性 | 性能 | 自动化载体 |
|-----------|:---:|:---:|:---:|:---:|:---:|-----------|
| 文件同步（双向） | ✅ | | | ✅ | | `e2e-test.sh` FS-10..13 |
| 分块上传（≥10MB） | ✅ | | | | | `e2e-test.sh` FS-04 |
| 版本历史 | ✅ | | | | | `e2e-test.sh` FS-05/13 |
| 冲突处理 | ✅ | | | | | `e2e-test.sh` FS-09 + 集成测试 |
| 回收站 | ✅ | | | | | `e2e-test.sh` FS-06 |
| 分享链接 | ✅ | ✅(密码哈希) | | | | `e2e-test.sh` FS-07 |
| 缩略图 | ✅ | ✅(路径穿越) | | | | `e2e-test.sh` FS-08 |
| WebSocket 实时推送 | ✅ | ✅(消息级认证) | | | | `WebSocketIntegrationTests` |
| Token 认证 | | ✅ | | | | `e2e-test.sh` SE-01/02 |
| 路径安全（ValidatePath） | | ✅ | | | | `e2e-test.sh` SE-03 |
| 速率限制 | | ✅ | | | | `e2e-test.sh` SE-04 |
| UDP 局域网发现 | | | | ✅ | | 手册（附录 A） |
| 服务端重启恢复 | | | ✅ | | | `e2e-test.sh` RE-01 |
| 客户端断线重连 | | | ✅ | | | `e2e-test.sh` RE-02 |
| Unicode 文件名 | | | | ✅ | | `e2e-test.sh` CO-01 |
| 大文件传输 | | | | | ✅ | 手册 🟡（机制由 FS-04 覆盖） |

## 3. 分层审查策略（测试金字塔映射）

按 ISO/IEC/IEEE 29119 测试级别 + 测试金字塔，投入比例 静态 ≫ 组件/集成 ≫ 系统 E2E：

```
┌─────────────────────────────────────────────┐
│ L4 验收     端到端审查报告（IEEE 829）         │  ← 本方案第 6 节
│ L3 系统 E2E 真实进程：e2e-test.sh + WS 测试   │  ← 本方案第 4 节（核心）
│ L2 组件/集成 xUnit + WebApplicationFactory   │  ← 复用 CloudPan.Tests
│ L1 静态门禁  CodeGen --verify + 编译 warnaserror + 分析器 + 架构测试 │ ← 复用 /check
└─────────────────────────────────────────────┘
```

| 层级 | 内容 | 失败动作 |
|------|------|---------|
| L1 静态门禁 | `dotnet run --project CloudPan.CodeGen -- --verify`、`dotnet build -c Release -warnaserror`（16 条 Roslyn 分析器 CP001-CP404）、架构测试 | 🔴 阻断，禁止进入 L3 |
| L2 组件/集成 | `dotnet test CloudPan.Tests -c Release --no-build`（~98 单测 + 16 集成 + 9 架构 + WS 测试） | 🔴 阻断 |
| L3 系统 E2E | `bash e2e-test.sh`（真实服务端+客户端进程） | 按场景逐项判定 |
| L4 验收 | 汇总报告 + 决策矩阵（第 6 节） | — |

> **不重复设计**：L1/L2 的细则已由 `.claude/skills/check/SKILL.md`、`.claude/release-verification-plan.md`（Stage 2-8）定义，本方案直接引用其命令，仅在 L3/L4 新增内容。

## 4. E2E 场景矩阵（核心章节）

每个场景含：**编号 / 前置 / 操作步骤 / 可观测断言（验收标准）/ ISO 25010 质量特性 / 阻断级别**。执行载体：`e2e-test.sh`（场景函数 `scenario_<ID>`）与 `CloudPan.Tests/Server/Controllers/WebSocketIntegrationTests.cs`。

### 4.0 生命周期覆盖矩阵（安装 → 配置 → 使用）

端到端审查必须覆盖产品完整生命周期，而非仅"已装好已配好"的运行中间态。矩阵映射每个阶段到场景：

| 生命周期阶段 | 覆盖场景 | 自动化载体 |
|:---:|---------|-----------|
| **安装** | IN-01 发布产物可运行（自包含单文件）、IN-02 Windows 服务（需管理员，手册）、IN-03 端口可达（8443 HTTP / 8450 UDP） | `e2e-test.sh` 阶段 0 |
| **配置** | CF-01 服务端首次启动自动生成 Token、CF-02 客户端配置持久化（DPAPI 回读 + 自动重连）、SetupForm/`/pair`/托盘配置（手册） | `e2e-test.sh` 阶段 0 + 附录 A |
| **使用** | FS-01..13 核心业务闭环、SE-01..04 安全、RE-01..02 可靠性、CO-01 兼容、WS-01..05 | `e2e-test.sh` 阶段 1-3 + WS 测试 |

> **边界说明**：依赖 UI 交互（WinForms `SetupForm`、托盘菜单、`/pair` 配对）或系统环境（管理员权限的 Windows 服务安装）的流程，v1.0 由附录 A 手册验收清单覆盖；v1.1 引入 FlaUI 后自动化。

### 4.1 服务端 API（功能适合性）

| 编号 | 场景 | 前置 | 操作 | 可观测断言 | 质量特性 | 阻断 |
|------|------|------|------|-----------|---------|:---:|
| FS-01 | 服务端健康就绪 | 启动服务端 | 轮询 `GET /api/health` | 60s 内返回 200 | 功能/可移植 | 🔴 |
| FS-02 | 小文件上传/下载 | 服务端就绪 | 上传 `/docs/upload.txt` → 下载 | 下载内容与上传逐字节一致（`cmp`） | 功能正确性 | 🔴 |
| FS-03 | 文件树列出 | FS-02 | `GET /api/files/tree` | 响应包含 upload.txt | 功能 | 🔴 |
| FS-04 | 分块上传大文件 | 服务端就绪 | 15MB 随机文件分 4 块（4MB/块）上传 → 下载 | 下载 SHA-256 与源一致 | 功能正确性 | 🔴 |
| FS-05 | 版本历史 | FS-02 | 上传 v2（baseVersion=1）→ `GET /api/versions` | 返回含 `version` 字段 | 功能 | 🔴 |
| FS-06 | 删除进回收站 | FS-05 | `POST /api/files/delete` → `GET /api/trash` | 回收站含 upload.txt | 功能 | 🔴 |
| FS-07 | 分享创建与公开下载 | FS-04 | 创建分享 → `GET /share/{id}/download` | 公开下载哈希与源一致 | 功能/互操作 | 🔴 |
| FS-08 | 缩略图生成 | 上传 1x1 PNG | `GET /api/thumbnails?path=...&width=64` | 返回 200 + image/jpeg | 功能 | 🟡 |
| FS-09 | 版本冲突检测 | 上传两版 | 用过时 baseVersion 上传 | 返回 409 | 功能正确性 | 🔴 |

### 4.2 安全性（OWASP ASVS L1 映射）

| 编号 | 场景 | 操作 | 可观测断言 | ASVS 映射 | 阻断 |
|------|------|------|-----------|----------|:---:|
| SE-01 | 无 Token | 无认证头请求受保护端点 | 401 | V2 认证 | 🔴 |
| SE-02 | 错误 Token | 携带错误 Token | 401 | V2 认证 | 🔴 |
| SE-03 | 路径穿越 | 上传 `path=../escape.txt`、下载 `path=../.cloudpan/server.db` | 均 400/403，元数据不可越界读取 | V1 输入验证 / V12 文件操作 | 🔴 |
| SE-04 | 速率限制 | 专用设备 80 次请求 `/api/files/tree` | 至少 1 次 429（60/min 滑动窗口） | V9 通信（限流） | 🟡 |

> 分享密码爆破防护（PBKDF2 慢哈希 + 分享端点按 IP 限流）不在真实进程套件中穷举验证，避免拉长运行时长；由单元测试 `SharePasswordHasherTests` 与代码审查覆盖。

### 4.3 客户端同步（功能/互操作）

| 编号 | 场景 | 操作 | 可观测断言 | 阻断 |
|------|------|------|-----------|:---:|
| FS-10 | 首次全量同步 | 客户端带 token 启动 | 服务端 big.bin 出现在客户端根，SHA-256 一致 | 🔴 |
| FS-11 | 客户端上传 | 客户端根新建文件 | 服务端文件树出现该文件 | 🔴 |
| FS-12 | 客户端删除 | 删除客户端根文件 | 服务端文件树不再包含该文件 | 🔴 |
| FS-13 | 客户端修改 | 连续修改客户端根文件 | 服务端产生版本记录 | 🔴 |

### 4.4 可靠性（ISO 25010 可靠性）

| 编号 | 场景 | 操作 | 可观测断言 | 阻断 |
|------|------|------|-----------|:---:|
| RE-01 | 服务端重启一致性 | 强杀服务端 → 重启（同 SyncRoot） | 重启后 health 200，big.bin 仍在文件树且下载哈希一致（DB+FS 不丢） | 🔴 |
| RE-02 | 客户端断线重连 | 服务端重启期间客户端保持运行 → 恢复后新建文件 | 客户端恢复同步，文件上传服务端（120s 内） | 🟡 |

### 4.5 兼容性

| 编号 | 场景 | 操作 | 可观测断言 | 阻断 |
|------|------|------|-----------|:---:|
| CO-01 | Unicode 文件名 | 上传 `/测试文档.txt` → 下载 | 文件名不损坏，内容一致 | 🟡 |

### 4.6 WebSocket（WebSocketIntegrationTests.cs）

| 编号 | 场景 | 操作 | 可观测断言 | 阻断 |
|------|------|------|-----------|:---:|
| WS-01 | 认证成功 | 连接 `/ws` → 发 `{token, deviceId}` | 收到 `auth_ok`，连接保持 Open | 🔴 |
| WS-02 | 错误 Token | 发错误 token | 收到 `auth_error` | 🔴 |
| WS-03 | 缺少 Token | 仅发 deviceId | 收到 `auth_error` | 🔴 |
| WS-04 | 实时推送 | 另一设备 HTTP 上传 | 收到 `file_changed` 事件（含 path） | 🔴 |
| WS-05 | 排除自身 | 同设备上传 | 不收到自身 `file_changed` | 🟡 |

### 4.7 安装与配置（生命周期）

| 编号 | 场景 | 操作 | 可观测断言 | 阻断 |
|------|------|------|-----------|:---:|
| IN-01 | 发布产物可运行 | `dotnet publish`（win-x64 自包含单文件）→ 运行发布 exe | health 200，与构建产物同行为 | 🔴 |
| IN-02 | Windows 服务安装 | `sc create` → `sc start` → 轮询 health → `sc stop`/`sc delete` | 服务启动并服务 HTTP（需管理员；否则转手册 🟡） | 🟡 |
| IN-03 | 端口可达 | HTTP 8443 health + UDP 8450 广播 `CLOUDPAN_DISCOVER` | UDP 收到含 `"server"` 的 JSON 响应 | 🔴 |
| CF-01 | 服务端首次启动 Token 生成 | 无预置 Token 启动（全新根）→ 读 `token.txt` | token.txt 生成；用该 Token 认证 API 返回 200 | 🔴 |
| CF-02 | 客户端配置持久化 | 预写 DPAPI 加密 `client-config.json` → 客户端**无参数**启动 | 客户端读配置自动连接，同步文件出现在服务端 | 🔴 |

> 客户端配置的**写入**路径（`SetupForm` 保存 → DPAPI 加密 → `client-config.json`）依赖 UI，归附录 A 手册验收；CF-02 验证的是**读取/解密/自动重连**这一运行时路径，两者互补。

## 5. 执行流水线与阻断级别

```
bash e2e-test.sh  （L1→L2 门禁通过后）
├─ [环境] 清理残留进程 → 建独立测试根 .e2e-test/
├─ 阶段 0  安装与配置    IN-01(发布产物) IN-03(UDP) IN-02(服务) CF-01(首启Token) CF-02(配置持久化)   （约 2-6min，含发布）
├─ 阶段 1  服务端 API    FS-01..09, CO-01, SE-01..04   （约 60-90s）
├─ 阶段 2  客户端同步    FS-10..13                     （约 45s）
└─ 阶段 3  可靠性        RE-01, RE-02                 （约 30-150s）
结果: PASS/FAIL/SKIP 逐场景输出 + 汇总 + 退出码（0=全绿，1=有失败，2=前置缺失）
```

**阻断级别决策**：
| 级别 | 条件 | 动作 |
|------|------|------|
| 🔴 阻断 | IN-01/03、CF-01/02、FS-01/02/03/04/05/06/07/09/10/11/12/13、SE-01/02/03、RE-01、WS-01/02/03/04 任一失败 | 视为"不能正常使用"，修复后重跑 |
| 🟡 条件 | FS-08、SE-04、RE-02、CO-01、WS-05、IN-02 失败/跳过 | 审查报告中披露原因，判断是否影响核心使用（IN-02 依赖管理员权限，跳过需转手册验收） |
| 🟢 通过 | 全部绿灯 | 结论"可以正常使用" |

**退出码**：`0` 全部通过（含合理跳过）；`1` 存在失败场景；`2` 前置（Release 未构建）缺失。

## 6. 证据与报告（IEEE 829）

每次审查产出一份测试汇总报告 `docs/e2e-review-report-YYYYMMDD.md`，结构：

```
1. 审查范围与目标          （对应 IEEE 829 Test Plan 摘要）
2. 执行环境                （OS / .NET SDK / 提交 SHA / 场景矩阵版本）
3. 结果总览                （各阶段 PASS/FAIL 计数 + 汇总表）
4. 失败/异常事件明细        （对应 IEEE 829 Test Incident Report：场景、观测、初步原因）
5. 结论与决策              （能否正常使用 + 阻断级别判定）
```

**证据留存**：
- 逐场景 PASS/FAIL（`e2e-test.sh` 控制台输出，可重定向 `tee` 存档）
- 服务端/客户端日志：`.e2e-test/server.log`、`client.log`（Serilog 结构化）
- 测试数据库与文件：`.e2e-test/server-root/.cloudpan/`（镜像文件可直接复查）

## 7. 与现有资产的关系

| 资产 | 关系 | 责任边界 |
|------|------|---------|
| `.claude/release-verification-plan.md` | **发布工程化验收**（编译/覆盖率/安全扫描/打包 11 Stage） | 本方案聚焦"运行中的产品能否正常使用"，两者互补，不重复 |
| `.claude/skills/check/SKILL.md`（/check） | 本方案 L1 静态门禁的执行载体 | 直接调用其 Step1-3 命令 |
| `.claude/skills/review/SKILL.md`（/review） | 变更后代码三维度审查（顺序/并发/异常） | 与 E2E 互补：审查查代码缺陷，E2E 验证运行行为 |
| `CloudPan.Analyzers/`（CP001-CP404） | 编译期防御 | L1 自动执行 |
| `CloudPan.Tests/`（xUnit + WebApplicationFactory） | L2 组件/集成层 | 本方案新增 WS 测试补缺口 |
| `.github/workflows/ci.yml` | CI 自动门禁（verify→build→test→publish） | 建议后续将 `e2e-test.sh` 与覆盖率门禁接入 CI（v1.1） |

## 8. 一键执行

```bash
# 1. 前置：静态门禁（契约一致 + 编译 + 单测）
cd e:/XiaoFeng/云盘
dotnet run --project CloudPan.CodeGen -- --verify
# 注意：-c Release 必不可少（CP301/CP200/CP303 的 WarningsNotAsErrors 豁免仅在 Release 生效）
dotnet build CloudPan.sln -c Release -p:TreatWarningsAsErrors=true
dotnet test CloudPan.Tests -c Release --no-build

# 2. 系统级 E2E（真实进程，覆盖安装/配置/使用全生命周期，约 5-10 分钟）
bash e2e-test.sh | tee .e2e-test/report-console.log

# 3. 完成附录 A 手册验收清单（UI/管理员权限流程）
# 4. 按第 6 节模板汇总，写入 docs/e2e-review-report-YYYYMMDD.md
```

## 附录 A：手册验收清单（依赖 UI / 管理员权限，v1.0 由人工执行）

> 覆盖场景矩阵中标"🟡/手册"或套件跳过（`⏭️`）的流程。清单执行结果记入审查报告。

```
审查版本: ________    日期: ________    测试人: ________

=== A1 安装 ===
[ ] publish.ps1 全量发布（win-x64 server+client+SETUP.bat）无报错
[ ] 运行 SETUP.bat 一键安装客户端到 %LOCALAPPDATA%\CloudPan，桌面/开始菜单快捷方式生成
[ ] （管理员）install-service.bat 安装 Windows 服务 CloudPanServer
[ ]   sc query CloudPanServer 状态 = RUNNING；健康检查 http://localhost:8443/api/health = 200
[ ] （管理员）sc stop / sc start 服务可正常停止/启动
[ ]   sc failure 已配置崩溃自动恢复（restart 5s/10s/60s）

=== A2 配置 ===
[ ] 服务端首次启动（无预置 Token）弹窗展示 Token，token.txt 生成且可复制
[ ] 客户端首次启动弹出 SetupForm：输入 serverUrl / 同步根 / Token → 保存
[ ]   重启客户端后凭保存配置自动连接（不弹配置窗）——验证 CF-02 的写入侧
[ ] 客户端配置保存在 %LOCALAPPDATA%\CloudPan\client-config.json（Token DPAPI 加密，非明文）
[ ] 修改设置（限速/选择路径）后生效
[ ] /pair 配对流程：浏览器访问 http://<ip>:8443/pair 完成配对

=== A3 使用（UI） ===
[ ] 托盘图标：在线（彩色）离线（灰色）状态切换正确
[ ] 托盘菜单：显示 Token / 打开设置 / 退出 各项可用
[ ] 设置窗体打开无异常，保存后重启仍生效
[ ] Android 客户端（v1.0 基础框架）：APK 安装、配置服务器、基础列表可显示
[ ] 断网后托盘转离线 → 恢复后自动转在线并补同步

=== A4 边界（可选，见 release-verification-plan.md 附录 A） ===
[ ] 100MB 大文件传输哈希一致
[ ] 3 个客户端并发操作互不干扰
[ ] NTFS 替代数据流/符号链接不传播

备注: _____________________________________________
```
