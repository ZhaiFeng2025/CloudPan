using CloudPan.Infrastructure.Design;

namespace CloudPan.Client.UI;

/// <summary>配置窗口校验协作类（T-109）：实时校验、文件夹安全检查、提交前完整校验与字段消息反馈。</summary>
internal sealed class SetupWizardValidation
{
    private enum MessageSeverity { Hint, Error }

    private readonly SetupForm _form;

    public SetupWizardValidation(SetupForm form)
    {
        _form = form;
    }

    /// <summary>检查文件夹是否安全可用——禁止系统目录、根目录、移动设备，并显示字段提示与文件统计。</summary>
    /// <param name="useHintColors">实时校验时用深灰提示色，提交时用红色。</param>
    public bool ValidateFolderSafetyWithFeedback(string folder, bool useHintColors = false)
    {
        // T-075：阻塞性安全校验下沉为共享静态方法（SetupForm/SettingsForm 复用）
        string? error = SetupForm.ValidateFolderSafety(folder);
        if (error != null)
        {
            ShowFieldMessage(_form._folderErrorLabel, error,
                useHintColors ? MessageSeverity.Hint : MessageSeverity.Error);
            return false;
        }

        // 安全校验通过后再取规范化路径，用于云盘接管提示与文件统计
        string normalized = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar);

        // 检查是否被其他同步服务接管（使用环境变量判断云盘路径）
        var cloudDrivePaths = new[]
        {
            (path: Environment.GetEnvironmentVariable("OneDrive"), name: "OneDrive"),
            (path: Environment.GetEnvironmentVariable("OneDriveConsumer"), name: "OneDrive"),
            (path: Environment.GetEnvironmentVariable("DROPBOX_HOME"), name: "Dropbox"),
            (path: Environment.GetEnvironmentVariable("iCloudDrive"), name: "iCloud"),
        };
        foreach (var (cloudPath, serviceName) in cloudDrivePaths)
        {
            if (!string.IsNullOrEmpty(cloudPath) && normalized.StartsWith(cloudPath, StringComparison.OrdinalIgnoreCase))
            {
                ShowFieldMessage(_form._folderErrorLabel,
                    $"此文件夹在 {serviceName} 同步范围内，可能造成同步冲突。确认要使用此文件夹吗？",
                    MessageSeverity.Hint); // 改为提示不阻断，让用户自行确认
                // 不 return false，仅提示
            }
        }

        // 统计文件夹内容（显示文件数量，帮助用户做决策）
        // 在后台线程执行枚举，前台最多等 2 秒，避免巨量文件阻塞 UI
        try
        {
            var (count, totalSize) = CountFolderContentsSafe(normalized);
            if (count > 0)
            {
                string sizeStr = totalSize > 1_048_576 ? $"{totalSize / 1_048_576} MB"
                    : totalSize > 1024 ? $"{totalSize / 1024} KB" : $"{totalSize} B";
                _form._folderErrorLabel.Text = count >= 10000
                    ? $"此文件夹包含超过 {count} 个文件，首次同步需要较长时间"
                    : count > 100
                    ? $"此文件夹包含 {count} 个文件（约 {sizeStr}），首次同步需要一些时间"
                    : $"此文件夹包含 {count} 个文件（{sizeStr}）";
                _form._folderErrorLabel.ForeColor = CloudPanColors.TextDarkGray;
                _form._folderErrorLabel.Visible = true;
            }
            else
            {
                HideFieldMessage(_form._folderErrorLabel);
            }
        }
        catch { /* 权限不足 —— 不干扰用户 */ }

        return true;
    }

    // ════════════════════════════════════════════════════════════════
    //  实时校验（失去焦点时显示柔和提示）
    // ════════════════════════════════════════════════════════════════

    public void ValidateServerUrlField()
    {
        string url = _form._serverUrlBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(url))
        {
            ShowFieldHint(_form._urlErrorLabel, "请输入服务端地址，如 http://192.168.1.100:8443");
            return;
        }
        if (!url.StartsWith("http://") && !url.StartsWith("https://"))
        {
            ShowFieldHint(_form._urlErrorLabel, "地址需以 http:// 或 https:// 开头");
            return;
        }
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || string.IsNullOrWhiteSpace(uri.Host)
            || uri.Port < 1 || uri.Port > 65535)
        {
            ShowFieldHint(_form._urlErrorLabel, "地址格式不正确（请检查 IP/域名和端口号 1-65535）");
            return;
        }
        if (!string.IsNullOrEmpty(uri.AbsolutePath) && uri.AbsolutePath != "/")
        {
            ShowFieldHint(_form._urlErrorLabel, "地址不应包含路径，只需 http://IP:端口");
            return;
        }
        HideFieldMessage(_form._urlErrorLabel);
    }

    public void ValidateSyncRootField()
    {
        string folder = _form._syncRootBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(folder))
        {
            ShowFieldHint(_form._folderErrorLabel, "请选择或输入同步文件夹路径");
            return;
        }
        if (folder.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            ShowFieldHint(_form._folderErrorLabel, "路径包含非法字符");
            return;
        }
        // 安全校验 + 统计信息，实时模式用深灰提示
        ValidateFolderSafetyWithFeedback(folder, useHintColors: true);
    }

    public void ValidateTokenField()
    {
        string token = _form._tokenBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(token))
        {
            ShowFieldHint(_form._tokenErrorLabel, "请输入家庭 Token");
            return;
        }
        if (token.Length != 64 || !IsHexString(token))
        {
            ShowFieldHint(_form._tokenErrorLabel, "Token 应为 64 个十六进制字符");
            return;
        }
        HideFieldMessage(_form._tokenErrorLabel);
    }

    // ════════════════════════════════════════════════════════════════
    //  OK 点击 —— 完整校验 + 提交
    // ════════════════════════════════════════════════════════════════

    public void OnOkClick(object? sender, EventArgs e)
    {
        // 搜索进行中禁止提交
        if (_form._isSearching)
        {
            ShowFieldHint(_form._statusLabel, "请等待搜索完成后再连接服务器");
            return;
        }

        _form._okButton.Enabled = false;
        if (!ValidateInputs())
        {
            _form._okButton.Enabled = true;
            return;
        }
        _form.DialogResult = DialogResult.OK;
        _form.Close();
    }

    private bool ValidateInputs()
    {
        bool valid = true;
        bool focusSet = false;

        // 服务端地址
        string url = _form.ServerUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            ShowFieldError(_form._urlErrorLabel, "请输入服务端地址");
            valid = false;
            if (!focusSet) { _form._serverUrlBox.Focus(); focusSet = true; }
        }
        else if (!url.StartsWith("http://") && !url.StartsWith("https://"))
        {
            ShowFieldError(_form._urlErrorLabel, "请输入完整地址，如 http://192.168.1.100:8443");
            valid = false;
            if (!focusSet) { _form._serverUrlBox.Focus(); focusSet = true; }
        }
        else if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
                 || string.IsNullOrWhiteSpace(uri.Host)
                 || uri.Port < 1 || uri.Port > 65535)
        {
            ShowFieldError(_form._urlErrorLabel, "地址格式不正确（请检查 IP/域名和端口号 1-65535）");
            valid = false;
            if (!focusSet) { _form._serverUrlBox.Focus(); focusSet = true; }
        }
        else
        {
            HideFieldMessage(_form._urlErrorLabel);
        }

        // 同步文件夹
        string folder = _form.SyncRoot;
        if (string.IsNullOrWhiteSpace(folder))
        {
            ShowFieldError(_form._folderErrorLabel, "请输入同步文件夹路径");
            valid = false;
            if (!focusSet) { _form._syncRootBox.Focus(); focusSet = true; }
        }
        else if (folder.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            ShowFieldError(_form._folderErrorLabel, "路径包含非法字符");
            valid = false;
            if (!focusSet) { _form._syncRootBox.Focus(); focusSet = true; }
        }
        else if (!ValidateFolderSafetyWithFeedback(folder, useHintColors: false))
        {
            valid = false;
            if (!focusSet) { _form._syncRootBox.Focus(); focusSet = true; }
        }
        else
        {
            HideFieldMessage(_form._folderErrorLabel);
        }

        // 家庭 Token
        string token = _form.Token;
        if (string.IsNullOrWhiteSpace(token))
        {
            ShowFieldError(_form._tokenErrorLabel, "请输入家庭 Token");
            valid = false;
            if (!focusSet) { _form._tokenBox.Focus(); focusSet = true; }
        }
        else if (token.Length != 64 || !IsHexString(token))
        {
            ShowFieldError(_form._tokenErrorLabel, "Token 格式不正确，请完整粘贴服务端显示的 64 个字符");
            valid = false;
            if (!focusSet) { _form._tokenBox.Focus(); focusSet = true; }
        }
        else
        {
            HideFieldMessage(_form._tokenErrorLabel);
        }

        return valid;
    }

    private static bool IsHexString(string s) =>
        !string.IsNullOrEmpty(s) && s.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'));

    /// <summary>
    /// 在后台线程统计文件夹内容，前台最多等待 2 秒。
    /// 超时或失败时返回 (0, 0) 以跳过详细统计显示。
    /// </summary>
    private static (int count, long totalSize) CountFolderContentsSafe(string normalized)
    {
        int count = 0;
        long totalSize = 0;
        Task<(int count, long totalSize)> task = Task.Run(() =>
        {
            foreach (string f in Directory.EnumerateFiles(normalized, "*", SearchOption.AllDirectories))
            {
                count++;
                if (count > 10000)
                {
                    return (count, totalSize);
                }

                if (count % 100 == 0)
                {
                    continue; // 每 100 个文件跳过 size 计算以节省时间
                }

                try { totalSize += new FileInfo(f).Length; } catch { }
            }
            return (count, totalSize);
        });
        if (task.Wait(TimeSpan.FromSeconds(2)))
        {
            return task.Result;
        }
        // 超时：返回 0 表示不显示详细统计
        return (0, 0);
    }

    // ════════════════════════════════════════════════════════════════
    //  字段消息辅助方法
    // ════════════════════════════════════════════════════════════════

    /// <summary>显示柔和提示（深灰色，用于实时校验和引导信息）。</summary>
    public static void ShowFieldHint(Label label, string text)
    {
        label.ForeColor = CloudPanColors.TextDarkGray;
        label.Text = text;
        label.Visible = true;
    }

    /// <summary>显示阻断错误（红色，用户提交时无效输入的反馈）。</summary>
    public static void ShowFieldError(Label label, string text)
    {
        label.ForeColor = CloudPanColors.ErrorRed;
        label.Text = text;
        label.Visible = true;
    }

    private static void ShowFieldMessage(Label label, string text, MessageSeverity severity)
    {
        if (severity == MessageSeverity.Error)
        {
            ShowFieldError(label, text);
        }
        else
        {
            ShowFieldHint(label, text);
        }
    }

    public static void HideFieldMessage(Label label)
    {
        label.Text = "";
        label.Visible = false;
    }

    // ================================================================
    // 字段校验具名事件处理器（CP301：避免匿名 lambda 订阅无法退订）
    // ================================================================

    public void ServerUrlBox_Leave(object? sender, EventArgs e) => ValidateServerUrlField();

    public void SyncRootBox_Leave(object? sender, EventArgs e) => ValidateSyncRootField();

    public void TokenBox_Leave(object? sender, EventArgs e) => ValidateTokenField();

    public void TokenBox_TextChanged(object? sender, EventArgs e)
    {
        string trimmed = _form._tokenBox.Text.Trim();
        if (trimmed != _form._tokenBox.Text)
        {
            _form._tokenBox.Text = trimmed;
        }
    }
}
