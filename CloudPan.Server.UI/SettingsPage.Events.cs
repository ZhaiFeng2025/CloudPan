using System.Net;
using System.Net.Sockets;
using CloudPan.Contract;
using CloudPan.Infrastructure.Configuration;
using CloudPan.Infrastructure.Design;
using CloudPan.Server.Host.Hosting;

namespace CloudPan.Server.UI;

/// <summary>SettingsPage 部分类：端口校验、浏览目录、Token 显示/复制/轮换与保存设置事件（具名方法，CP301）。</summary>
public partial class SettingsPage
{
    // ===== 端口校验 =====
    private static void NumericOnly_KeyPress(object? sender, KeyPressEventArgs e)
    {
        if (!char.IsDigit(e.KeyChar) && !char.IsControl(e.KeyChar)) e.Handled = true;
    }

    /// <summary>探测端口是否被占用（不含当前生效端口——本进程正在监听）。</summary>
    private static bool IsPortInUse(int port, int currentEffectivePort)
    {
        if (port == currentEffectivePort) return false;
        try
        {
            var listener = new TcpListener(IPAddress.Any, port);
            listener.Start();
            listener.Stop();
            return false;
        }
        catch (SocketException)
        {
            return true;
        }
    }

    // ===== 事件处理 =====
    private void BrowseBtn_Click(object? sender, EventArgs e)
    {
        if (sender is not Button { Tag: TextBox box })
        {
            return;
        }
        using FolderBrowserDialog dialog = new FolderBrowserDialog
        {
            Description = "选择同步根目录",
            SelectedPath = Directory.Exists(box.Text) ? box.Text : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            UseDescriptionForTitle = true
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            box.Text = dialog.SelectedPath;
        }
    }

    private void ToggleTokenMask_Click(object? sender, EventArgs e)
    {
        _tokenBox.UseSystemPasswordChar = !_tokenBox.UseSystemPasswordChar;
        _toggleTokenBtn.Text = _tokenBox.UseSystemPasswordChar ? "显示" : "隐藏";
    }

    private void CopyToken_Click(object? sender, EventArgs e)
    {
        if (string.IsNullOrEmpty(_tokenBox.Text))
        {
            SetStatus("Token 尚未生成", CloudPanColors.WarningOrange);
            return;
        }
        try { Clipboard.SetText(_tokenBox.Text); SetStatus("Token 已复制到剪贴板", CloudPanColors.SuccessGreen); }
        catch (Exception ex) { SetStatus($"复制失败: {ex.Message}", CloudPanColors.ErrorRed); }
    }

    private async void RotateBtn_Click(object? sender, EventArgs e)
    {
        // F-34/T-034：轮换前列出影响面（所有已配对设备需重配），避免设备静默断连
        var result = ConfirmTokenRotation(await BuildDeviceImpactAsync());
        if (result != DialogResult.OK)
        {
            return;
        }
        _rotateBtn.Enabled = false;
        try
        {
            string newToken = await _tokenService.RotateAsync(_disconnectCheck.Checked);
            _tokenBox.Text = newToken;
            ServerTrayApp.Token = newToken;
            _log("连接钥匙已轮换（旧连接钥匙已失效）");
            SetStatus("轮换成功，旧连接钥匙已失效。点击「复制」获取新连接钥匙并分发给所有设备", CloudPanColors.SuccessGreen);
        }
        catch (Exception ex)
        {
            _log($"连接钥匙轮换失败: {ex.Message}");
            SetStatus($"轮换失败: {ex.Message}", CloudPanColors.ErrorRed);
        }
        finally
        {
            _rotateBtn.Enabled = true;
        }
    }

    // async void 仅在 UI 事件处理器使用；顶层 try-catch 覆盖方法体（CLAUDE.md 7.2）
    private async void SaveBtn_Click(object? sender, EventArgs e)
    {
        try
        {
            // 收集并校验 Startup 持久化设置（端口/同步根目录），经 ServerSettingsFile 保存、重启生效；AppConfig 运行时设置经轮换动作即时写入
            BootstrapSettings settings = new(null, null);
            foreach (ServerSettingDef def in SpecSettings.All)
            {
                if (def.Persistence != SettingPersistence.Startup)
                    continue;
                string raw = _startupBoxes[def.Key].Text.Trim();
                switch (def.Type)
                {
                    case SettingType.Int:
                        if (!int.TryParse(raw, out int newPort)
                            || (def.Min.HasValue && newPort < def.Min.Value)
                            || (def.Max.HasValue && newPort > def.Max.Value))
                        {
                            SetStatus($"{def.Label} 必须是 {def.Min}-{def.Max} 的整数", CloudPanColors.ErrorRed);
                            return;
                        }
                        if (def.Key == SpecSettings.Keys.Port && IsPortInUse(newPort, _effectivePort))
                        {
                            SetStatus($"端口 {newPort} 已被占用，请更换端口", CloudPanColors.ErrorRed);
                            return;
                        }
                        settings = settings with { Port = newPort };
                        break;
                    case SettingType.String:
                        string fullNewValue;
                        try
                        {
                            if (string.IsNullOrWhiteSpace(raw)) throw new ArgumentException("不能为空");
                            fullNewValue = def.IsPath ? Path.GetFullPath(raw) : raw;
                        }
                        catch (Exception ex)
                        {
                            SetStatus($"{def.Label} 无效: {ex.Message}", CloudPanColors.ErrorRed);
                            return;
                        }
                        settings = settings with { SyncRoot = fullNewValue };
                        break;
                    case SettingType.Secret:
                        // Secret 运行时设置由对应 Action 处理（token 轮换），不在保存按钮写入
                        break;
                }
            }

            // 同步根目录变更：列出影响面 + 明确不迁移 + 重配引导（F-34/T-034）+ 旧服务启动参数提示
            if (settings.SyncRoot != null)
            {
                bool syncRootChanged = !string.Equals(
                    Path.GetFullPath(_currentSyncRoot), settings.SyncRoot, StringComparison.OrdinalIgnoreCase);
                if (syncRootChanged)
                {
                    string impact = await BuildDeviceImpactAsync();
                    var warning = ConfirmSyncRootChange(impact);
                    if (warning != DialogResult.OK)
                    {
                        return;
                    }
                    // 旧安装的 binPath 带 --SyncRoot 会覆盖设置文件——提示重装迁移
                    if (TrayAppRunner.IsServiceInstalled("CloudPanServer")
                        && ServiceRestartHelper.ServiceHasLegacyBinPathParam("--SyncRoot"))
                    {
                        MessageBox.Show(
                            "检测到服务启动参数含旧的 --SyncRoot，会覆盖此处设置的同步根目录。\n\n" +
                            "请重新运行安装向导（或删除后重装 CloudPanServer 服务），以让新目录生效。",
                            "服务配置提示", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
                    }
                }
            }

            // 保存（Startup 设置经 ServerSettingsFile）
            try
            {
                ServerSettingsFile.Save(settings);
                SetStatus("已保存，重启服务后生效", CloudPanColors.SuccessGreen);
                _log($"设置已保存：端口 {settings.Port}，同步根目录 {settings.SyncRoot}");
                ServiceRestartHelper.PromptRestart();
            }
            catch (Exception ex)
            {
                SetStatus($"保存失败: {ex.Message}", CloudPanColors.ErrorRed);
            }
        }
        catch (Exception ex)
        {
            SetStatus($"保存失败: {ex.Message}", CloudPanColors.ErrorRed);
        }
    }
}
