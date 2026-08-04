using System.Drawing;
using System.Windows.Forms;
using CloudPan.Client.Core.Services;
using CloudPan.Contract;
using CloudPan.Infrastructure.Design;

namespace CloudPan.Client.UI;

/// <summary>
/// 回收站恢复冲突可操作引导（T-094/F-136 拆分）：恢复失败不再静默 AddLog——
/// 目标被同名重建（服务端 409 CONFLICT）时弹白话原因 + 三选项（改名恢复/覆盖/取消）并自动收敛，
/// 其余失败也弹可见提示。独立 internal 类以满足 MainWindow 聚合行数门禁，由 MainWindow.Trash 恢复路径调用。
/// </summary>
internal static class RestoreConflictDialog
{
    /// <summary>
    /// 恢复回收站条目。目标同名冲突时弹白话原因 + 覆盖/改名/取消选项并自动收敛；失败均可见提示。
    /// 返回 true=恢复成功。
    /// </summary>
    public static async Task<bool> RestoreAsync(
        SyncEngine engine, Action<string> addLog, TrashItem item, IWin32Window owner)
    {
        try
        {
            bool ok = await engine.RestoreTrashAsync(item);
            if (!ok)
            {
                // 非冲突失败（网络/服务端异常）也弹可见提示，具体原因在日志
                MessageBox.Show(owner, $"恢复失败：{DisplayName(item)} 未能恢复，请稍后重试。",
                    "恢复失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            return ok;
        }
        catch (System.Net.Http.HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            bool? mode = Show(item, owner);
            if (mode == null)
            {
                addLog($"已取消恢复: {item.OriginalPath}");
                return false;
            }
            bool resolved = await engine.RestoreTrashResolveAsync(item,
                mode.Value ? RestoreConflictMode.RenameTarget : RestoreConflictMode.Overwrite);
            if (!resolved)
            {
                addLog($"恢复冲突处理失败: {item.OriginalPath}");
                MessageBox.Show(owner, "恢复失败：同名文件冲突处理未成功（目标可能已再次变化），请到原位置确认后再试。",
                    "恢复冲突", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            return resolved;
        }
        catch (Exception ex)
        {
            addLog($"恢复失败: {ex.Message}");
            MessageBox.Show(owner, $"恢复失败：{ex.Message}", "恢复失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
    }

    /// <summary>显示恢复冲突对话框：白话原因 + 三选项。返回 true=改名恢复，false=覆盖恢复，null=取消。</summary>
    private static bool? Show(TrashItem item, IWin32Window owner)
    {
        var baseFont = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;
        using var dlg = new Form
        {
            Text = "恢复冲突",
            Size = new Size(470, 200),
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
            BackColor = CloudPanColors.BackgroundWhite,
        };
        var label = new Label
        {
            Text = $"已有同名文件，恢复会造成覆盖。\n\n文件：{DisplayName(item)}\n目标位置：{item.OriginalPath}\n\n请选择处理方式：",
            AutoSize = true,
            Location = new Point(16, 16),
            MaximumSize = new Size(430, 0),
            ForeColor = CloudPanColors.TextPrimary,
            Font = new Font(baseFont.FontFamily, CloudPanFonts.SizeBody),
        };
        void OnRenameClick(object? s, EventArgs e) { dlg.DialogResult = DialogResult.Yes; dlg.Close(); }
        void OnOverwriteClick(object? s, EventArgs e) { dlg.DialogResult = DialogResult.No; dlg.Close(); }
        void OnCancelClick(object? s, EventArgs e) { dlg.DialogResult = DialogResult.Cancel; dlg.Close(); }
        Button renameBtn = new Button { Text = "改名恢复", Width = 96, Height = CloudPanSpacing.MinTouchSize, FlatStyle = FlatStyle.Flat };
        renameBtn.FlatAppearance.BorderColor = CloudPanColors.ButtonBorderGray;
        renameBtn.Click += OnRenameClick;
        Button overwriteBtn = new Button { Text = "覆盖", Width = 88, Height = CloudPanSpacing.MinTouchSize, FlatStyle = FlatStyle.Flat };
        overwriteBtn.FlatAppearance.BorderColor = CloudPanColors.ButtonBorderGray;
        overwriteBtn.Click += OnOverwriteClick;
        Button cancelBtn = new Button { Text = "取消", Width = 88, Height = CloudPanSpacing.MinTouchSize, FlatStyle = FlatStyle.Flat };
        cancelBtn.FlatAppearance.BorderColor = CloudPanColors.ButtonBorderGray;
        cancelBtn.Click += OnCancelClick;
        var btnPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 52,
            Padding = new Padding(8),
        };
        btnPanel.Controls.Add(cancelBtn);
        btnPanel.Controls.Add(overwriteBtn);
        btnPanel.Controls.Add(renameBtn);
        dlg.Controls.Add(label);
        dlg.Controls.Add(btnPanel);
        return dlg.ShowDialog(owner) switch
        {
            DialogResult.Yes => true,    // 改名恢复
            DialogResult.No => false,    // 覆盖恢复
            _ => null,                   // 取消
        };
    }

    /// <summary>回收站条目显示名（原始路径最后一段）。</summary>
    private static string DisplayName(TrashItem t)
    {
        string p = t.OriginalPath.TrimEnd('/');
        return p[(p.LastIndexOf('/') + 1)..];
    }
}
