# CloudPan 安装指南

## 系统要求

- Windows 10/11（64 位）
- [.NET 8 Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)
- 家庭局域网环境

## 安装方式

### 方式一：从源码运行（开发者推荐）

```bash
git clone https://github.com/cloudpan/cloudpan.git
cd cloudpan
dotnet run --project CloudPan.Server.Host
```

### 方式二：发布版安装

#### 服务端

1. 编译发布：
```powershell
dotnet publish CloudPan.Server.Host -c Release -o publish/server
```

2. 以管理员身份运行安装脚本：
```bash
install-service.bat
```

该脚本会：
- 将 CloudPan 安装为 Windows Service（开机自启）
- 添加防火墙规则（TCP 8443 入站）
- 配置崩溃自动恢复（24h 内 3 次重启）
- 在 `%USERPROFILE%\CloudPan` 创建同步目录

3. 获取家庭共享 Token：
   - Token 保存在 `%USERPROFILE%\CloudPan\.cloudpan\token.txt`
   - 首次启动后服务端控制台也会显示（仅一次）

#### 客户端

1. 编译发布：
```powershell
dotnet publish CloudPan.Client.UI -c Release -o publish/client
```

2. 直接运行 `CloudPan.Client.exe`，或在命令行指定参数：
```bash
CloudPan.Client.exe http://192.168.1.100:8443 C:\CloudPan <token>
```

### 未安装 .NET Runtime？

如需独立部署（无需安装 .NET Runtime）：
```powershell
dotnet publish CloudPan.Server.Host -c Release -o publish/server --self-contained true -r win-x64
dotnet publish CloudPan.Client.UI -c Release -o publish/client --self-contained true -r win-x64
```
> 注意：独立部署包体积约 80MB。

## 服务管理

安装为 Windows Service 后的常用命令：

```bash
# 启动服务
sc start CloudPanServer

# 停止服务
sc stop CloudPanServer

# 查看状态
sc query CloudPanServer

# 卸载服务
sc stop CloudPanServer
sc delete CloudPanServer
```

## 防火墙配置

CloudPan 使用以下端口：
- **TCP 8443**：HTTP 服务端口（API + WebSocket + 管理面板）
- **UDP 8450**：局域网设备发现

Windows 防火墙规则在 `install-service.bat` 中自动添加。其他防火墙软件需手动放行上述端口。

## 配置文件位置

| 配置项 | 路径 |
|---|---|
| 服务端数据库 | `{SyncRoot}\.cloudpan\server.db` |
| 服务端设置 | 服务端 exe 目录的 `server-settings.json`（端口/同步根目录） |
| 客户端数据库 | `{SyncRoot}\.cloudpan\client.db` |
| 服务端日志 | `{SyncRoot}\.cloudpan\logs\server-*.log` |
| 客户端配置 | `%LOCALAPPDATA%\CloudPan\client-config.json` |
| 版本历史 | `{SyncRoot}\.cloudpan\.versions\` |
| 缩略图缓存 | `{SyncRoot}\.cloudpan\.thumbnails\` |
| 回收站 | `{SyncRoot}\.cloudpan\.trash\` |

## 常见问题

### 客户端无法连接服务端
1. 确认两台电脑在同一局域网
2. 检查服务端防火墙是否放行 TCP 8443
3. 确认服务端正在运行（托盘图标绿色 ✓）
4. 使用局域网 IP 地址而非 localhost

### Token 忘记了
Token 保存在服务端同步目录的 `.cloudpan\token.txt` 文件中。

### 数据库损坏
删除 `{SyncRoot}\.cloudpan\server.db`（或 `client.db`），重新启动会自动重建。
> 注意：删除数据库会丢失同步状态和版本历史，但不会删除已同步的文件。

### Windows Service 启动失败
1. 检查是否以管理员身份运行安装
2. 查看 Windows 事件查看器 → Windows 日志 → 应用程序
3. 确认 `CloudPan.Server.exe` 路径正确
4. 检查 .NET 8 Runtime 是否已安装

### 端口被占用
默认端口 8443 可能被其他程序占用。改端口有三种方式（优先级从高到低）：
1. **设置页**（推荐）：管理窗口「设置」页修改端口，重启服务生效；或手动编辑服务端 exe 目录的 `server-settings.json`。
2. **启动参数**：以 `--Port 9443` 启动（命令行优先于设置文件）。
3. **修改默认值**：改 `shared-spec.json → config.httpPort`，重跑 `dotnet run --project CloudPan.CodeGen`，重新编译。
