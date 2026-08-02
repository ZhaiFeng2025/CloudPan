# CloudPan — 自托管家庭文件同步系统

CloudPan 是一个自托管的家庭文件同步系统，让你在自己的 Windows 电脑上运行私有云盘服务端，其他设备（笔记本、台式机）自动同步文件。

**v1.0.0 正式发布版** — 核心功能完整可用。

## 特性

- 🔄 **实时文件同步**：台式机放文件 → 笔记本自动出现，支持双向同步
- 🗂️ **版本历史**：保留最近 5 个版本，支持随时回滚
- 🗑️ **回收站**：30 天自动清理，误删可恢复
- 🔗 **分享链接**：生成限时/限量下载链接分享给家人
- 📦 **大文件分块上传**：自动处理 >10MB 文件，支持断点续传
- 🖼️ **图片缩略图**：浏览照片时自动生成缩略图
- 📊 **管理面板**：Web 界面查看文件、设备、日志、统计
- 🔍 **UDP 局域网发现**：客户端自动发现服务端，无需手动输入 IP
- 🛡️ **Token 认证**：家庭共享 Token + DPAPI 加密存储
- 💻 **Windows 原生体验**：系统托盘常驻、开机自启、右键菜单

## 系统要求

- **服务端**：Windows 10/11、.NET 8 Runtime
- **客户端**：Windows 10/11、.NET 8 Runtime
- **网络**：家庭局域网（HTTP，Phase 0 未启用 TLS）

## 快速开始

### 1. 安装 .NET 8 Runtime

从 [https://dotnet.microsoft.com/download/dotnet/8.0](https://dotnet.microsoft.com/download/dotnet/8.0) 下载安装。

### 2. 启动服务端

```bash
# 克隆或下载本项目
git clone https://github.com/cloudpan/cloudpan.git
cd cloudpan

# 编译并运行
dotnet run --project CloudPan.Server
```

首次运行会自动生成 64 字符家庭共享 Token（仅显示一次，请妥善保存）。

### 3. 安装客户端

```bash
# 在同一台或另一台电脑上
dotnet run --project CloudPan.Client
```

首次运行时会弹出配置窗口，输入：
- 服务端地址（如 `http://192.168.1.100:8443`）
- 同步根目录
- 家庭共享 Token

### 4. 开始同步

配置完成后，客户端自动连接服务端并开始同步。在同步根目录中放入文件，其他设备上会自动出现。

## 项目结构

四层依赖架构（Host/UI → Core → Infrastructure → Contract，单向）：

```
CloudPan.sln
├── CloudPan.Contract/        # 契约层：spec 生成物（DTO/枚举/端点/错误码）+ 传输协议，零 UI 依赖
├── CloudPan.Infrastructure/  # 基础设施层：持久化/文件存储/路径安全/密钥（两端共用）
├── CloudPan.Server.Core/     # 服务端领域层：索引/版本/分享/回收站/WebSocket
├── CloudPan.Server.Host/     # 服务端宿主：HTTP 适配 + 中间件 + 定时任务 + 组合根
├── CloudPan.Server.UI/       # 服务端管理 UI（托盘/窗口/安装器，可选）
├── CloudPan.Client.Core/     # 客户端领域层：同步引擎/队列/传输客户端
├── CloudPan.Client.UI/       # 客户端 WinForms 壳
├── CloudPan.CodeGen/         # 契约代码生成器
├── CloudPan.Analyzers/       # Roslyn 自定义分析器
├── CloudPan.Tests/           # 测试项目（含架构测试）
└── CloudPan.Android/         # Android 客户端（原型）
```

完整架构规格见 [docs/architecture-requirements.md](docs/architecture-requirements.md)。

## 文档

- [安装指南](INSTALL.md) — Windows Service 安装、防火墙配置
- [使用手册](使用手册.html) — 完整功能说明和故障排除

## 构建与测试

```bash
# 构建
dotnet build

# 测试
dotnet test

# Release 构建
dotnet build -c Release

# 代码生成校验
dotnet run --project CloudPan.CodeGen -- --verify
```

## 许可

MIT License
