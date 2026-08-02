using System.Drawing.Drawing2D;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using CloudPan.Contract;
using CloudPan.Infrastructure.Design;

namespace CloudPan.Client.UI;

/// <summary>SetupForm 部分类：输入校验、文件夹安全检查与提交。</summary>
public partial class SetupForm
{

    // ════════════════════════════════════════════════════════════════
    //  文件夹安全验证（保持原逻辑不变）
    // ════════════════════════════════════════════════════════════════

    /// <summary>检查文件夹是否安全可用——禁止系统目录、根目录、移动设备。</summary>
    /// <param name="useHintColors">实时校验时用深灰提示色，提交时用红色。</param>
    private bool ValidateFolderSafety(string folder, bool useHintColors = false)
    {
        try
        {
            string normalized = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar);
            bool isRoot = Path.GetPathRoot(normalized) == normalized;
            if (isRoot)
            {
                ShowFieldMessage(_folderErrorLabel, "不能选择磁盘根目录，请选择具体文件夹",
                    useHintColors ? MessageSeverity.Hint : MessageSeverity.Error);
                return false;
            }

            // 禁止系统目录
            string sysRoot = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
            string progFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string progFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            if (normalized.StartsWith(sysRoot, StringComparison.OrdinalIgnoreCase)
                || normalized.StartsWith(progFiles, StringComparison.OrdinalIgnoreCase)
                || normalized.StartsWith(progFilesX86, StringComparison.OrdinalIgnoreCase))
            {
                ShowFieldMessage(_folderErrorLabel, "不能选择系统目录，请选择用户文件夹",
                    useHintColors ? MessageSeverity.Hint : MessageSeverity.Error);
                return false;
            }

            // 禁止可移动磁盘和网络驱动器
            DriveInfo drive = new DriveInfo(Path.GetPathRoot(normalized)!);
            if (drive.DriveType == DriveType.Network)
            {
                ShowFieldMessage(_folderErrorLabel, "不支持网络驱动器，请选择本地文件夹",
                    useHintColors ? MessageSeverity.Hint : MessageSeverity.Error);
                return false;
            }
            if (drive.DriveType == DriveType.Removable)
            {
                ShowFieldMessage(_folderErrorLabel, "不支持移动磁盘，请选择内置硬盘上的文件夹",
                    useHintColors ? MessageSeverity.Hint : MessageSeverity.Error);
                return false;
            }

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
                    ShowFieldMessage(_folderErrorLabel,
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
                    _folderErrorLabel.Text = count >= 10000
                        ? $"此文件夹包含超过 {count} 个文件，首次同步需要较长时间"
                        : count > 100
                        ? $"此文件夹包含 {count} 个文件（约 {sizeStr}），首次同步需要一些时间"
                        : $"此文件夹包含 {count} 个文件（{sizeStr}）";
                    _folderErrorLabel.ForeColor = CloudPanColors.TextDarkGray;
                    _folderErrorLabel.Visible = true;
                }
                else
                {
                    HideFieldMessage(_folderErrorLabel);
                }
            }
            catch { /* 权限不足 —— 不干扰用户 */ }

            return true;
        }
        catch (Exception ex)
        {
            ShowFieldMessage(_folderErrorLabel, $"路径无效: {ex.Message}",
                useHintColors ? MessageSeverity.Hint : MessageSeverity.Error);
            return false;
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  实时校验（失去焦点时显示柔和提示）
    // ════════════════════════════════════════════════════════════════

    private void ValidateServerUrlField()
    {
        string url = _serverUrlBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(url))
        {
            ShowFieldHint(_urlErrorLabel, "请输入服务端地址，如 http://192.168.1.100:8443");
            return;
        }
        if (!url.StartsWith("http://") && !url.StartsWith("https://"))
        {
            ShowFieldHint(_urlErrorLabel, "地址需以 http:// 或 https:// 开头");
            return;
        }
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || string.IsNullOrWhiteSpace(uri.Host)
            || uri.Port < 1 || uri.Port > 65535)
        {
            ShowFieldHint(_urlErrorLabel, "地址格式不正确（请检查 IP/域名和端口号 1-65535）");
            return;
        }
        if (!string.IsNullOrEmpty(uri.AbsolutePath) && uri.AbsolutePath != "/")
        {
            ShowFieldHint(_urlErrorLabel, "地址不应包含路径，只需 http://IP:端口");
            return;
        }
        HideFieldMessage(_urlErrorLabel);
    }

    private void ValidateSyncRootField()
    {
        string folder = _syncRootBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(folder))
        {
            ShowFieldHint(_folderErrorLabel, "请选择或输入同步文件夹路径");
            return;
        }
        if (folder.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            ShowFieldHint(_folderErrorLabel, "路径包含非法字符");
            return;
        }
        // 安全校验 + 统计信息，实时模式用深灰提示
        ValidateFolderSafety(folder, useHintColors: true);
    }

    private void ValidateTokenField()
    {
        string token = _tokenBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(token))
        {
            ShowFieldHint(_tokenErrorLabel, "请输入家庭 Token");
            return;
        }
        if (token.Length != 64 || !IsHexString(token))
        {
            ShowFieldHint(_tokenErrorLabel, "Token 应为 64 个十六进制字符");
            return;
        }
        HideFieldMessage(_tokenErrorLabel);
    }

    // ════════════════════════════════════════════════════════════════
    //  OK 点击 —— 完整校验 + 提交
    // ════════════════════════════════════════════════════════════════

    private void OnOkClick(object? sender, EventArgs e)
    {
        // 搜索进行中禁止提交
        if (_isSearching)
        {
            ShowFieldHint(_statusLabel, "请等待搜索完成后再连接服务器");
            return;
        }

        _okButton.Enabled = false;
        if (!ValidateInputs())
        {
            _okButton.Enabled = true;
            return;
        }
        DialogResult = DialogResult.OK;
        Close();
    }

    private bool ValidateInputs()
    {
        bool valid = true;
        bool focusSet = false;

        // 服务端地址
        string url = ServerUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            ShowFieldError(_urlErrorLabel, "请输入服务端地址");
            valid = false;
            if (!focusSet) { _serverUrlBox.Focus(); focusSet = true; }
        }
        else if (!url.StartsWith("http://") && !url.StartsWith("https://"))
        {
            ShowFieldError(_urlErrorLabel, "请输入完整地址，如 http://192.168.1.100:8443");
            valid = false;
            if (!focusSet) { _serverUrlBox.Focus(); focusSet = true; }
        }
        else if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
                 || string.IsNullOrWhiteSpace(uri.Host)
                 || uri.Port < 1 || uri.Port > 65535)
        {
            ShowFieldError(_urlErrorLabel, "地址格式不正确（请检查 IP/域名和端口号 1-65535）");
            valid = false;
            if (!focusSet) { _serverUrlBox.Focus(); focusSet = true; }
        }
        else
        {
            HideFieldMessage(_urlErrorLabel);
        }

        // 同步文件夹
        string folder = SyncRoot;
        if (string.IsNullOrWhiteSpace(folder))
        {
            ShowFieldError(_folderErrorLabel, "请输入同步文件夹路径");
            valid = false;
            if (!focusSet) { _syncRootBox.Focus(); focusSet = true; }
        }
        else if (folder.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            ShowFieldError(_folderErrorLabel, "路径包含非法字符");
            valid = false;
            if (!focusSet) { _syncRootBox.Focus(); focusSet = true; }
        }
        else if (!ValidateFolderSafety(folder, useHintColors: false))
        {
            valid = false;
            if (!focusSet) { _syncRootBox.Focus(); focusSet = true; }
        }
        else
        {
            HideFieldMessage(_folderErrorLabel);
        }

        // 家庭 Token
        string token = Token;
        if (string.IsNullOrWhiteSpace(token))
        {
            ShowFieldError(_tokenErrorLabel, "请输入家庭 Token");
            valid = false;
            if (!focusSet) { _tokenBox.Focus(); focusSet = true; }
        }
        else if (token.Length != 64 || !IsHexString(token))
        {
            ShowFieldError(_tokenErrorLabel, "Token 格式不正确，请完整粘贴服务端显示的 64 个字符");
            valid = false;
            if (!focusSet) { _tokenBox.Focus(); focusSet = true; }
        }
        else
        {
            HideFieldMessage(_tokenErrorLabel);
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

    private enum MessageSeverity { Hint, Error }

    /// <summary>显示柔和提示（深灰色，用于实时校验和引导信息）。</summary>
    private static void ShowFieldHint(Label label, string text)
    {
        label.ForeColor = CloudPanColors.TextDarkGray;
        label.Text = text;
        label.Visible = true;
    }

    /// <summary>显示阻断错误（红色，用户提交时无效输入的反馈）。</summary>
    private static void ShowFieldError(Label label, string text)
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

    private static void HideFieldMessage(Label label)
    {
        label.Text = "";
        label.Visible = false;
    }

    // ================================================================
    // 字段校验具名事件处理器（CP301：避免匿名 lambda 订阅无法退订）
    // ================================================================

    private void ServerUrlBox_Leave(object? sender, EventArgs e) => ValidateServerUrlField();

    private void SyncRootBox_Leave(object? sender, EventArgs e) => ValidateSyncRootField();

    private void TokenBox_Leave(object? sender, EventArgs e) => ValidateTokenField();

    private void TokenBox_TextChanged(object? sender, EventArgs e)
    {
        string trimmed = _tokenBox.Text.Trim();
        if (trimmed != _tokenBox.Text)
        {
            _tokenBox.Text = trimmed;
        }
    }
}
