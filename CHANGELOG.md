# Changelog

本文件记录 CloudPan 各版本的变更。格式遵循 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.0.0/) 约定。版本号与 `shared-spec.json` 顶层 `version` 对齐（契约唯一事实来源）。

## [1.6.0] - 2026-08-03

请求体契约化（T-067）——entities 新增非持久化 API 请求 DTO（DeleteRequest/MoveRequest/MkdirRequest/CreateShareRequest/RestoreTrashRequest/RestoreRequest），字段名对齐 apiMapping；DtoGenerator 生成请求 record，服务端 Controller 删除文件内手写 record、客户端 ApiClient 删除匿名对象请求体，统一引用生成类型。

## [1.5.0] - 2026-08-03

断点续传健壮性（T-064）：

- `api.responses.ChunkStatusData` 新增 `version: int`（服务端当前版本号），断点续传恢复路径不再兜底 version=0
- `entities.ChunkedUpload` 新增 `Finalized` 布尔列：Finalize 完成标记，崩溃窗口（位图已收全块但未落盘）会话重启时清除并允许客户端重传

## [1.4.0] - 2026-07-31

认证模型与端点完备化：

- `endpoints[].auth` 从 boolean 升级为字符串枚举 `token|public|localhost|message`
- 补齐 11 个未注册端点：/admin×5 + /api/trash×3 + /api/version + /api/cert-fingerprint + /pair
- `HttpErrorCode` 新增 `INVALID_DEVICE_ID` (400)
- 新增 `api.errorResponse` 统一错误体格式（消除控制器 2 字段 vs 中间件 3 字段不一致）
- 新增 `api.responses` 段：定义 5 个 API 响应包装类型
- `api.websocket` 新增 `authMode: message`（明确 /ws 认证在首条消息，非 HTTP 头）
- config 新增 `httpPort: 8443`、`udpDiscoveryPort: 8450`
- `entities.SyncQueue.fields` 新增 `TargetPath`（TEXT nullable，客户端重命名目标）

## [1.3.0] - 2026-07-28

路径安全与 WebSocket 端点契约化：

- 所有文件大小字段 `csharpType: long`，防止 >2GB 截断
- `api.endpoints` 注册 WebSocket 端点 /ws（GET, auth=message）；SpecRoutes 生成 WebSocket 常量，Program.cs 与 WebSocketClient 改引用，删除手拼 /ws
- 服务端 Controller 全部路由字面量改引用 SpecRoutes 生成常量，类级 [Route] 前缀移除，路由单一事实来源为契约（T-058）

## [1.2.0] - 2026-07-28

响应 DTO 契约化与 Android 备份字段：

- `api.responses` 补齐全端点响应 DTO：admin/files+devices+logs+stats、api/devices、health、version、cert-fingerprint、files delete/move/mkdir/search、trash restore/empty
- 服务端控制器匿名对象响应全部改用生成响应 DTO（T-040），响应体单一事实来源为 ApiResponses.g.cs
- Server.Core 删除与生成 DTO 重复的响应记录，改为引用契约生成类型
- 新增 `BackupStatus` 枚举（Android 照片备份状态）；`BackupLog.Status` 引用枚举替代内联值
- `ChunkedUpload` 新增 `DeviceId` 字段 + `idx_chunk_device` 索引
- `api.rateLimit` 添加 `_ref` 指向 config.rateLimitPerMinute，消除重复定义
- config 新增 `websocketReconnectMaxBackoffSeconds`；`_comments` 标注 retryBackoffMs 仅用于 HTTP API
- `api.websocket` 心跳字段重命名为 `pingIntervalSeconds`/`pongTimeoutSeconds`；新增 `_note` 说明 Android

## [1.1.0] - 2026-08-02

服务端设置子系统 v1——管理窗口新增"设置"页签，三个设置项全部可写。

### 新增

- **设置页**：管理窗口加"设置"页签（网络/存储/安全三区），托盘菜单加"设置"入口
- **Token 轮换**：立即生效；三处同步（DB 哈希权威源 / token.txt 尽力而为 / 内存缓存立即失效），轮换后旧 Token 即刻失效；可选断开所有已连接设备
- **端口可配置**：`server-settings.json`（exe 目录）持久化，重启生效；CLI `--Port` 优先；UDP 局域网发现广播 URL 联动新端口
- **同步根目录可改**：写入设置文件，重启生效；旧目录 `.cloudpan` 不迁移（含强警告）；检测旧安装 binPath 残留 `--SyncRoot` 并提示重装迁移
- **契约驱动**：`shared-spec.json` 新增顶层 `settings` 段，CodeGen 生成 `Settings.g.cs`；`version` 升至 1.1.0

### 缺陷修复

- 托盘"显示/复制 Token"服务重启后失效（Token 静态字段仅首次启动赋值）——回退读 token.txt
- `SecretStore` Token 文件 ACL 仅授 ReadData/WriteData，同步句柄读取被拒（Access denied）——改为当前用户 FullControl（安全语义不变）
- 测试修复：`FileTreeResponse.Data` 迁移为数组后 `FileIndexServiceTests` 使用 `.Count` 编译失败——改 `.Length`

## [1.0.0] - 2026-08-02

自托管家庭文件同步系统 v1.0.0 正式发布版。

### 核心功能

- **双向文件同步**：FileSystemWatcher 主通道 + 5 分钟定时全量扫描兜底；SHA-256 传输前比对与下载后校验
- **分块上传**：≥10MB 自动分块（4MB/块）、断点续传、合并时哈希校验 + 冲突副本
- **版本历史**：全局单调递增版本号，`.versions/` 存档，最多保留 5 版
- **冲突处理**：baseVersion 比对，冲突生成 `_冲突_yyyyMMdd_HHmmss` 副本
- **回收站**：删除进 `.trash/`，30 天自动清理，可恢复/清空
- **分享链接**：公开 `/share/{id}/download`，PBKDF2 带盐密码哈希 + 按 IP 限流防爆破
- **缩略图**：SkiaSharp 生成 JPEG 缓存至 `.thumbnails/`
- **WebSocket 实时推送**：消息级认证、心跳、file_changed/deleted/renamed/conflict 事件
- **Token 认证**：64 字符家庭共享 Token，SHA-256 哈希存储，SecretStore 权限保护
- **速率限制**：60 次/分钟/设备（滑动窗口），上传下载不计数
- **UDP 局域网发现**：端口 8450 响应 `CLOUDPAN_DISCOVER` 广播
- **管理面板**：设备列表、SyncLog 审计日志、配对页（localhost 限定）
- **Android 客户端原型**：Kotlin + Compose，文件浏览/搜索/上传下载/照片备份基础框架

### 架构与工程质量

- **四层架构落地**：Contract → Infrastructure → Core → Host/UI 严格单向依赖（架构测试守护）
- **契约驱动代码生成**：`shared-spec.json` 为唯一事实来源，`CloudPan.CodeGen` 生成 DTO/枚举/实体/端点/错误体
- **Roslyn 分析器防御体系**：CP001-CP404 共 16 条规则，编译期拦截反模式（中间件顺序/匿名事件订阅/路径穿越等）
- **测试**：~105 项单元/集成/架构测试 + 26 项真实进程端到端场景（安装/配置/使用全生命周期）
- **CI**：GitHub Actions（契约校验 → 编译 → 测试 → 发布）

### 缺陷修复（本版本累计）

- 数值溢出：`CurrentSize/Size/FileSize` int→long（>2GB 文件静默截断）
- 安全：缩略图路径穿越、分享密码弱哈希、命令注入、设备注册唯一约束竞态
- DB+FS 一致性：上传存档入事务、删除先 DB 后 FS 兜底、回收站恢复事务化、孤儿版本清理
- 并发/生命周期：Timer 回调 async 安全、WebSocket 重连竞态、IDisposable/CTS 配对、下载无限重试
- 客户端：代理拦截局域网、无配置误弹窗口、DI 循环依赖、UI 线程死锁、`.tmp` 文件被误上传
- 托盘 UX：单击无响应、窗口不可见、内存泄漏

### 已知限制

- **未启用 TLS**：家庭局域网内 HTTP 明文传输，已在文档声明；v1.1 计划引入 TLS + 自签名证书 + 指纹 pinning
- **未启用自动更新**：计划 v1.1
- 客户端 UI 配置（SetupForm/托盘/配对）暂以人工验收为主，UI 自动化计划 v1.1（FlaUI）

### 工程化建议（待 v1.1）

- 覆盖率门禁当前未达标（全局 ~16%，目标 ≥65%），需分优先级补测（WebSocketHandler/控制器/客户端传输层）
- `/check` 门禁命令已对齐 CI（`-c Release -p:TreatWarningsAsErrors`）
- 基准测试已修复入口与 SHA-256 路径缺陷，可正常执行

[1.0.0]: 暂以 `shared-spec.json`/csproj 版本 1.0.0 为准；git tag 历史存在 v1.0.2-v1.0.6，版本治理需统一。
