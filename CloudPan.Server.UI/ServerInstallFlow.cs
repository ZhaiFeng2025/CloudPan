using System.Net.NetworkInformation;
using CloudPan.Contract;
using CloudPan.Infrastructure.Design;
using CloudPan.Infrastructure.Security;

namespace CloudPan.Server.UI;

/// <summary>安装向导安装流程协作类（T-110）：Windows 服务注册/进程执行/完成后的地址与 Token 展示。逻辑从 ServerInstaller 外提。</summary>
internal sealed class ServerInstallFlow
{
    private readonly ServerInstaller _form;
    private readonly ServerInstallSteps _steps;

    public ServerInstallFlow(ServerInstaller form, ServerInstallSteps steps)
    {
        _form = form;
        _steps = steps;
    }

    // =================================================================
    //  安装流程
    // =================================================================
    internal async Task InstallAsync()
    {
        _form._installBtn.Enabled = false;
        _form._progressBar.Visible = true;
        _steps.SetStatusText("正在安装服务...");
        _form._statusLabel.ForeColor = CloudPanColors.TextSecondary;

        try
        {
            // 同步目录输入验证
            string syncDir = _form._syncDirBox.Text.Trim();
            if (string.IsNullOrEmpty(syncDir))
            {
                syncDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "CloudPan");
            }
            else if (syncDir.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            {
                _steps.SetStatusText("同步目录路径包含非法字符，请重新输入。");
                _form._statusLabel.ForeColor = CloudPanColors.ErrorRed;
                _form._installBtn.Enabled = true;
                return;
            }

            // 检查是否已存在 .cloudpan 元数据目录（重复安装风险）
            string existingMeta = Path.Combine(syncDir, ".cloudpan");
            if (Directory.Exists(existingMeta))
            {
                var overwrite = MessageBox.Show(
                    $"同步目录「{syncDir}」中已存在 .cloudpan 元数据目录。\n\n" +
                    "是否继续安装？（如果之前安装过，继续可能导致数据混乱）",
                    "CloudPan — 元数据目录已存在",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (overwrite != DialogResult.Yes)
                {
                    _steps.SetStatusText("已取消安装。");
                    _form._installBtn.Enabled = true;
                    return;
                }
            }

            if (!Directory.Exists(syncDir))
            {
                Directory.CreateDirectory(syncDir);
            }

            // ========================================
            // 第一步：清理旧服务（忽略错误）
            // ========================================
            _steps.SetStep(0);
            _steps.SetStatusText("正在清理旧服务...");
            await Task.Run(() => RunExe("sc.exe", "stop", "CloudPanServer"));
            await Task.Run(() => RunExe("sc.exe", "delete", "CloudPanServer"));

            // ========================================
            // 第二步：创建新服务
            // ========================================
            _steps.SetStep(1);
            _steps.SetStatusText("正在创建服务...");
            string exePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "CloudPan.Server.exe");
            if (!File.Exists(exePath))
            {
                exePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Server", "CloudPan.Server.exe");
            }

            if (!File.Exists(exePath))
            {
                _steps.SetStatusText($"未找到服务可执行文件：{exePath}\n请确保程序文件完整，然后重试。");
                _form._statusLabel.ForeColor = CloudPanColors.ErrorRed;
                _form._progressBar.Visible = false;
                _form._installBtn.Enabled = true;
                return;
            }

            // 直接调用 sc.exe（绕过 cmd.exe），ArgumentList 自动处理特殊字符转义，消除命令注入风险
            bool createOk = await Task.Run(() =>
                RunExe("sc.exe", "create", "CloudPanServer",
                    "binPath=", $"\"{exePath}\" --SyncRoot {syncDir}",
                    "DisplayName=", "CloudPan 文件同步服务",
                    "start=", "auto"));
            if (!createOk)
            {
                string errDetail = GetLastCmdError() ?? "";
                _steps.SetStatusText($"创建服务失败：{errDetail}\n1. 是否以管理员身份运行\n2. 可执行文件路径是否正确\n然后点击「开始安装」重试");
                _form._statusLabel.ForeColor = CloudPanColors.ErrorRed;
                _form._progressBar.Visible = false;
                _form._installBtn.Enabled = true;
                return;
            }

            await Task.Run(() => RunExe("sc.exe", "description", "CloudPanServer", "CloudPan File Sync Service"));

            // M-04: 崩溃自动恢复
            await Task.Run(() =>
                RunExe("sc.exe", "failure", "CloudPanServer", "reset=86400", "actions=restart/5000/restart/10000/restart/60000"));

            // ========================================
            // 第三步：启动服务（失败则回滚）
            // ========================================
            _steps.SetStep(2);
            _steps.SetStatusText("正在启动服务...");
            bool startOk = await Task.Run(() => RunExe("sc.exe", "start", "CloudPanServer"));
            // 轮询等待服务进入 RUNNING 状态（首次启动可能因初始化延迟）
            bool serviceReady = false;
            for (int i = 0; i < 15; i++)
            {
                await Task.Delay(1000);
                bool queryOk = RunCmd("sc query CloudPanServer | find \"RUNNING\"");
                if (queryOk) { serviceReady = true; break; }
            }

            if (!startOk && !serviceReady)
            {
                string errDetail = GetLastCmdError() ?? "";
                _steps.SetStatusText($"服务启动失败：{errDetail}，正在回滚...");
                await Task.Run(() => RunExe("sc.exe", "stop", "CloudPanServer"));
                await Task.Run(() => RunExe("sc.exe", "delete", "CloudPanServer"));
                // 注意：防火墙规则尚未添加（步骤 3），此处不删除
                _form._statusLabel.ForeColor = CloudPanColors.ErrorRed;
                _form._progressBar.Visible = false;
                _form._installBtn.Enabled = true;
                return;
            }

            // ========================================
            // 第四步：防火墙规则（失败不阻塞）
            // ========================================
            _steps.SetStep(3);
            _steps.SetStatusText("正在添加防火墙规则...");
            await Task.Run(() => RunExe("netsh.exe", "advfirewall", "firewall", "delete", "rule", "name=CloudPan"));
            await Task.Run(() =>
                RunExe("netsh.exe", "advfirewall", "firewall", "add", "rule",
                    "name=CloudPan", "dir=in", "action=allow", "protocol=TCP", $"localport={SpecPorts.HttpPort}"));
            _steps.SetStatusText("已添加防火墙规则");

            // ========================================
            // 第五步：等待 Token 生成
            // ========================================
            _steps.SetStep(4);
            _steps.SetStatusText("等待服务初始化... (已等待 0 秒)");
            string tokenPath = Path.Combine(syncDir, ".cloudpan", "token.txt");

            for (int i = 0; i < 30; i++)
            {
                await Task.Delay(1000);
                _steps.SetStatusText($"等待服务初始化... (已等待 {i + 1} 秒)");
                if (File.Exists(tokenPath))
                {
                    string token;
                    try { token = SecretStore.ReadToken(syncDir) ?? ""; }
                    catch (IOException)
                    {
                        // 服务正在写入 token 文件，等待下一轮重试
                        continue;
                    }
                    if (!string.IsNullOrEmpty(token))
                    {
                        // 显示 Token 并触发成功动画
                        _form._tokenBox.Text = FormatToken(token);
                        _form._tokenArea.Visible = true;
                        _steps.SetStep(5); // 全部完成
                        _form._statusLabel.ForeColor = CloudPanColors.SuccessGreen;
                        string serverAddr = GetLocalIpAddress();
                        _steps.SetStatusText("安装成功！请在笔记本上运行 CloudPan.Client.exe");
                        AddServerAddressLabel(serverAddr, _form._tokenPanel);
                        _steps.FlashSuccessBorder();
                        break;
                    }
                }
            }

            if (!_form._tokenBox.IsDisposed && string.IsNullOrEmpty(_form._tokenBox.Text))
            {
                // 超时：不调用 SetStep(5)，保留步骤 4 蓝色状态，视觉上表明"未完成"
                _steps.SetStatusText($"安装完成但 Token 未就绪（Token 将在服务首次启动后生成）\n请稍后查看：{syncDir}\\.cloudpan\\token.txt");
                _form._statusLabel.ForeColor = CloudPanColors.WarningOrange;
            }

            _form._progressBar.Visible = false;
            _form._installBtn.Visible = false;
            _form._closeBtn.Visible = true;
            _form._closeBtn.Focus();
            _form.DialogResult = DialogResult.OK; // 通知调用方安装成功，应退出进程避免端口冲突
        }
        catch (Exception ex)
        {
            _steps.SetStatusText($"安装失败: {ex.Message}\n已自动尝试回滚，请修复后重试。");
            _form._statusLabel.ForeColor = CloudPanColors.ErrorRed;
            await Task.Run(() => RunExe("sc.exe", "stop", "CloudPanServer"));
            await Task.Run(() => RunExe("sc.exe", "delete", "CloudPanServer"));
            // 防火墙规则可能尚未添加，delete 已幂等，留作清理
            await Task.Run(() => RunExe("netsh.exe", "advfirewall", "firewall", "delete", "rule", "name=CloudPan"));
            _form._progressBar.Visible = false;
            _form._installBtn.Enabled = true;
        }
    }


    // =================================================================
    //  辅助方法
    // =================================================================

    /// <summary>
    /// 获取本机局域网 IP 地址
    /// </summary>
    private static string GetLocalIpAddress()
    {
        try
        {
            var first = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                            n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .FirstOrDefault(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
            return first?.Address.ToString() ?? "127.0.0.1";
        }
        catch
        {
            return "127.0.0.1";
        }
    }

    /// <summary>
    /// 在 Token 面板中添加服务端地址显示
    /// </summary>
    private void AddServerAddressLabel(string ip, Panel panel)
    {
        Label addrLabel = new Label
        {
            Text = $"服务端地址: http://{ip}:{SpecPorts.HttpPort}",
            Location = new Point(10, 56),
            AutoSize = true,
            Font = new Font(CloudPanFonts.FontFamilyMono, CloudPanFonts.SizeMono),
            ForeColor = CloudPanColors.TextPrimary,
            Cursor = Cursors.Hand
        };
        Label hintLabel = new Label
        {
            Text = "（点击复制地址）",
            Location = new Point(10, 72),
            AutoSize = true,
            Font = new Font(CloudPanFonts.FontFamily, CloudPanFonts.SizeCaption),
            ForeColor = CloudPanColors.TextMuted
        };
        addrLabel.Tag = ip;
        addrLabel.Click += AddrLabel_Click;
        panel.Controls.Add(addrLabel);
        panel.Controls.Add(hintLabel);
    }

    /// <summary>点击服务端地址标签：复制地址并短暂显示"已复制"。</summary>
    private void AddrLabel_Click(object? sender, EventArgs e)
    {
        var addrLabel = (Label)sender!;
        string ip = addrLabel.Tag as string ?? "";
        try { Clipboard.SetText($"http://{ip}:{SpecPorts.HttpPort}"); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"复制地址失败: {ex.Message}"); }
        addrLabel.Text = $"服务端地址: http://{ip}:{SpecPorts.HttpPort} ✓ 已复制";
        Task.Run(async () =>
        {
            try { await Task.Delay(1500); } catch { }
            try
            {
                if (!addrLabel.IsDisposed)
                {
                    addrLabel.Invoke(() =>
                    {
                        if (!addrLabel.IsDisposed)
                        {
                            addrLabel.Text = $"服务端地址: http://{ip}:{SpecPorts.HttpPort}";
                        }
                    });
                }
            }
            catch (ObjectDisposedException) { }
        });
    }

    /// <summary>
    /// 将 Token 每 16 字符一组用短横分隔显示
    /// </summary>
    private static string FormatToken(string token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return token;
        }

        return string.Join("-",
            Enumerable.Range(0, (token.Length + 15) / 16)
                .Select(i => token.Substring(i * 16, Math.Min(16, token.Length - i * 16))));
    }

    // =================================================================
    //  进程执行（sc/netsh 直调与 cmd 管道），统一经 ProcessRunner 单点
    // =================================================================

    /// <summary>
    /// 直接运行可执行文件（绕过 cmd.exe），经 ProcessRunner 的 ArgumentList 自动转义。
    /// 失败时错误详情通过 LastRunCmdError 存储。
    /// </summary>
    private static bool RunExe(string exeName, params string[] args)
    {
        ProcessResult result = ProcessRunner.Run(exeName, args);
        LastRunCmdError = result.ErrorMessage;
        return result.Success;
    }

    /// <summary>
    /// 仅用于需要 cmd 管道（如 | find）的命令——参数必须硬编码、无用户输入。
    /// 切勿将用户可控的路径/文本传入此方法。
    /// </summary>
    private static bool RunCmd(string command)
    {
        ProcessResult result = ProcessRunner.Run("cmd.exe", null,
            new ProcessRunnerOptions { UseCmd = true, CmdCommand = command });
        LastRunCmdError = result.ErrorMessage;
        return result.Success;
    }

    private static string? LastRunCmdError;

    /// <summary>获取最近一次命令调用的错误详情。</summary>
    private static string? GetLastCmdError() => LastRunCmdError;
}
