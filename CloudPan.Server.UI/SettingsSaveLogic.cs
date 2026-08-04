using CloudPan.Contract;
using CloudPan.Infrastructure.Configuration;
using CloudPan.Infrastructure.Design;
using CloudPan.Server.Host.Hosting;

namespace CloudPan.Server.UI;

/// <summary>设置页保存/轮换协作类（T-110）：收集并校验 Startup 设置经 ServerSettingsFile 保存，Token 轮换即时写入。逻辑从 SettingsPage 事件外提。</summary>
internal sealed class SettingsSaveLogic
{
    private readonly SettingsPage _form;
    private readonly SettingsPageGuides _guide;

    public SettingsSaveLogic(SettingsPage form, SettingsPageGuides guide)
    {
        _form = form;
        _guide = guide;
    }

    /// <summary>
    /// 保存 Startup 持久化设置（端口/同步根目录），经 ServerSettingsFile 持久化、重启生效；AppConfig 运行时设置经轮换动作即时写入。
    /// 保存前校验取值/端口占用，同步根变更时列出影响面并引导重配（F-34/T-034）。
    /// </summary>
    internal async Task SaveAsync()
    {
        // 收集并校验 Startup 持久化设置
        BootstrapSettings settings = new(null, null);
        foreach (ServerSettingDef def in SpecSettings.All)
        {
            if (def.Persistence != SettingPersistence.Startup)
                continue;
            string raw = _form._startupBoxes[def.Key].Text.Trim();
            switch (def.Type)
            {
                case SettingType.Int:
                    if (!int.TryParse(raw, out int newPort)
                        || (def.Min.HasValue && newPort < def.Min.Value)
                        || (def.Max.HasValue && newPort > def.Max.Value))
                    {
                        _form.SetStatus($"{def.Label} 必须是 {def.Min}-{def.Max} 的整数", CloudPanColors.ErrorRed);
                        return;
                    }
                    if (def.Key == SpecSettings.Keys.Port && IsPortInUse(newPort, _form._effectivePort))
                    {
                        _form.SetStatus($"端口 {newPort} 已被占用，请更换端口", CloudPanColors.ErrorRed);
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
                        _form.SetStatus($"{def.Label} 无效: {ex.Message}", CloudPanColors.ErrorRed);
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
                Path.GetFullPath(_form._currentSyncRoot), settings.SyncRoot, StringComparison.OrdinalIgnoreCase);
            if (syncRootChanged)
            {
                string impact = await _guide.BuildDeviceImpactAsync();
                var warning = _guide.ConfirmSyncRootChange(impact);
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
            _form.SetStatus("已保存，重启服务后生效", CloudPanColors.SuccessGreen);
            _form._log($"设置已保存：端口 {settings.Port}，同步根目录 {settings.SyncRoot}");
            ServiceRestartHelper.PromptRestart();
        }
        catch (Exception ex)
        {
            _form.SetStatus($"保存失败: {ex.Message}", CloudPanColors.ErrorRed);
        }
    }

    /// <summary>轮换连接钥匙（Token）：F-34/T-034 轮换前列出影响面，成功后写入新 Token 并广播。</summary>
    internal async Task RotateAsync()
    {
        // F-34/T-034：轮换前列出影响面（所有已配对设备需重配），避免设备静默断连
        var result = _guide.ConfirmTokenRotation(await _guide.BuildDeviceImpactAsync());
        if (result != DialogResult.OK)
        {
            return;
        }
        _form._rotateBtn.Enabled = false;
        try
        {
            string newToken = await _form._tokenService.RotateAsync(_form._disconnectCheck.Checked);
            _form._tokenBox.Text = newToken;
            ServerTrayApp.Token = newToken;
            _form._log("连接钥匙已轮换（旧连接钥匙已失效）");
            _form.SetStatus("轮换成功，旧连接钥匙已失效。点击「复制」获取新连接钥匙并分发给所有设备", CloudPanColors.SuccessGreen);
        }
        catch (Exception ex)
        {
            _form._log($"连接钥匙轮换失败: {ex.Message}");
            _form.SetStatus($"轮换失败: {ex.Message}", CloudPanColors.ErrorRed);
        }
        finally
        {
            _form._rotateBtn.Enabled = true;
        }
    }

    /// <summary>探测端口是否被占用（不含当前生效端口——本进程正在监听）。</summary>
    private static bool IsPortInUse(int port, int currentEffectivePort)
    {
        if (port == currentEffectivePort) return false;
        try
        {
            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Any, port);
            listener.Start();
            listener.Stop();
            return false;
        }
        catch (System.Net.Sockets.SocketException)
        {
            return true;
        }
    }
}
