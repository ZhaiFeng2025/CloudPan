using CloudPan.Infrastructure.Design;

namespace CloudPan.Server.UI;

/// <summary>SettingsPage 部分类：端口校验、浏览目录、Token 显示/复制与保存/轮换事件（具名方法，CP301；业务逻辑经 SettingsSaveLogic 外提）。</summary>
public partial class SettingsPage
{
    // ===== 端口校验 =====
    internal static void NumericOnly_KeyPress(object? sender, KeyPressEventArgs e)
    {
        if (!char.IsDigit(e.KeyChar) && !char.IsControl(e.KeyChar)) e.Handled = true;
    }

    // ===== 事件处理（具名方法，CP301；逻辑外提至 SettingsFormBuilder/SettingsSaveLogic） =====
    internal void BrowseBtn_Click(object? sender, EventArgs e)
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

    internal void ToggleTokenMask_Click(object? sender, EventArgs e)
    {
        _tokenBox.UseSystemPasswordChar = !_tokenBox.UseSystemPasswordChar;
        _toggleTokenBtn.Text = _tokenBox.UseSystemPasswordChar ? "显示" : "隐藏";
    }

    internal void CopyToken_Click(object? sender, EventArgs e)
    {
        if (string.IsNullOrEmpty(_tokenBox.Text))
        {
            SetStatus("Token 尚未生成", CloudPanColors.WarningOrange);
            return;
        }
        try { Clipboard.SetText(_tokenBox.Text); SetStatus("Token 已复制到剪贴板", CloudPanColors.SuccessGreen); }
        catch (Exception ex) { SetStatus($"复制失败: {ex.Message}", CloudPanColors.ErrorRed); }
    }

    // async void 仅在 UI 事件处理器使用；顶层 try-catch 覆盖方法体（CLAUDE.md 7.2），业务逻辑经 _save.RotateAsync 外提
    internal async void RotateBtn_Click(object? sender, EventArgs e)
    {
        try
        {
            await _save.RotateAsync();
        }
        catch (Exception ex)
        {
            _log($"连接钥匙轮换失败: {ex.Message}");
            SetStatus($"轮换失败: {ex.Message}", CloudPanColors.ErrorRed);
        }
    }

    // async void 仅在 UI 事件处理器使用；顶层 try-catch 覆盖方法体（CLAUDE.md 7.2），业务逻辑经 _save.SaveAsync 外提
    internal async void SaveBtn_Click(object? sender, EventArgs e)
    {
        try
        {
            await _save.SaveAsync();
        }
        catch (Exception ex)
        {
            SetStatus($"保存失败: {ex.Message}", CloudPanColors.ErrorRed);
        }
    }
}
