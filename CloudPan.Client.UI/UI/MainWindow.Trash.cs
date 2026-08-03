using System.Diagnostics;
using System.Drawing.Drawing2D;
using CloudPan.Client.Core.Models;
using CloudPan.Client.Core.Services;
using CloudPan.Contract;
using CloudPan.Infrastructure.Design;

namespace CloudPan.Client.UI;

/// <summary>MainWindow 部分类：删除进回收站、撤销 Snackbar 与最近删除对话框。</summary>
public partial class MainWindow
{

    // ================================================================
    // 删除进回收站 + 撤销 + 最近删除入口（T-014）
    // ================================================================

    /// <summary>从回收站条目原始路径取显示名（最后一段）。</summary>
    private static string TrashDisplayName(TrashItem t)
    {
        string p = t.OriginalPath.TrimEnd('/');
        return p[(p.LastIndexOf('/') + 1)..];
    }

    /// <summary>T-014/T-083/T-092：文件浏览「删除」/「批量删除」→ 全部进回收站（服务端软删墓碑+移入回收站），本地副本即时删除，显示撤销 Snackbar（5 秒）。批量删除（多选）前弹确认对话框（对齐 Android AlertDialog），单个删除不弹。</summary>
    private async void FileBrowser_DeleteRequested(IReadOnlyList<FileBrowseItem> items)
    {
        try
        {
            // T-092：批量删除（多选）前弹确认，防止误触全选；单个删除不弹（对齐 Android AlertDialog 行为）
            if (items.Count > 1 &&
                MessageBox.Show(this, $"将删除 {items.Count} 项，移入回收站可恢复。", "删除确认",
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK)
            {
                return;
            }

            var trashed = new List<TrashItem>();
            foreach (FileBrowseItem item in items)
            {
                TrashItem? trashItem = await _engine.DeleteForTrashAsync(item.Path);
                AddLog(trashItem != null ? $"已删除（可撤销）: {item.Name}" : $"已删除: {item.Name}");
                if (trashItem != null)
                {
                    trashed.Add(trashItem);
                }
            }

            if (trashed.Count > 0)
            {
                _lastDeletedTrashItems = trashed;
                // T-092：提示可到回收站恢复，避免用户误以为删除不可撤销
                _undoLabel.Text = trashed.Count == 1
                    ? $"已删除 “{TrashDisplayName(trashed[0])}”，可在 5 秒内撤销，也可到回收站恢复"
                    : $"已删除 {trashed.Count} 个项目，可在 5 秒内撤销，也可到回收站恢复";
                _undoBar.Visible = true;
                _undoBar.BringToFront();
                _undoTimer.Stop();
                _undoTimer.Start();
            }
            else
            {
                _undoBar.Visible = false;
            }

            await LoadBrowserAsync();
        }
        catch (Exception ex)
        {
            AddLog($"批量删除失败: {ex.Message}");
        }
    }

    /// <summary>T-014：点击「回收站」→ 打开最近删除入口（列表/恢复/清空，复用服务端三端点）。</summary>
    private async void FileBrowser_TrashRequested()
    {
        try
        {
            List<TrashItem> items = await _engine.GetTrashAsync();
            ShowTrashDialog(items);
        }
        catch (Exception ex)
        {
            AddLog($"打开回收站失败: {ex.Message}");
        }
    }

    /// <summary>T-014/T-083：点击撤销 → 恢复最近删除的全部回收站条目（5 秒窗口内有效）。</summary>
    private async void UndoButton_Click(object? sender, EventArgs e)
    {
        _undoTimer.Stop();
        _undoBar.Visible = false;
        List<TrashItem> items = _lastDeletedTrashItems;
        _lastDeletedTrashItems = new();
        if (items.Count == 0)
        {
            return;
        }

        try
        {
            bool allOk = true;
            foreach (TrashItem item in items)
            {
                bool ok = await _engine.RestoreTrashAsync(item);
                AddLog(ok ? $"已撤销删除，恢复文件: {item.OriginalPath}" : $"撤销失败: {item.OriginalPath}");
                allOk &= ok;
            }
            if (allOk)
            {
                await LoadBrowserAsync();
            }
        }
        catch (Exception ex)
        {
            AddLog($"撤销失败: {ex.Message}");
        }
    }

    /// <summary>T-014：撤销窗口超时 → 隐藏 Snackbar，丢弃撤销机会。</summary>
    private void UndoTimer_Tick(object? sender, EventArgs e)
    {
        _undoTimer.Stop();
        _undoBar.Visible = false;
        _lastDeletedTrashItems = new();
    }

    /// <summary>最近删除对话框：列出回收站条目，支持恢复选中 / 清空回收站（复用 /api/trash 三端点）。</summary>
    private void ShowTrashDialog(List<TrashItem> items)
    {
        var baseFont = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;

        Form dialog = new Form
        {
            Text = "回收站（最近删除）",
            Size = new Size(620, 420),
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            ShowInTaskbar = false,
        };

        ListView list = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            HideSelection = false,
            BorderStyle = BorderStyle.None,
            BackColor = CloudPanColors.BackgroundWhite,
            ForeColor = CloudPanColors.TextPrimary,
            Font = new Font(baseFont.FontFamily, CloudPanFonts.SizeBody),
        };
        list.Columns.Add("名称", 270);
        list.Columns.Add("大小", 90);
        list.Columns.Add("删除时间", 150);

        // 空回收站提示标签
        Label emptyLabel = new Label
        {
            Dock = DockStyle.Fill,
            Text = "回收站是空的",
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = CloudPanColors.TextMuted,
            Font = new Font(baseFont.FontFamily, CloudPanFonts.SizeBody),
            BackColor = CloudPanColors.BackgroundWhite,
            Visible = items.Count == 0,
        };

        static string DisplayName(TrashItem t)
        {
            string p = t.OriginalPath.TrimEnd('/');
            return p[(p.LastIndexOf('/') + 1)..];
        }

        void RefreshList(List<TrashItem> data)
        {
            list.BeginUpdate();
            try
            {
                list.Items.Clear();
                foreach (TrashItem t in data)
                {
                    ListViewItem lvi = new ListViewItem(DisplayName(t)) { Tag = t };
                    lvi.SubItems.Add(t.IsDirectory ? "" : UiFormat.FormatFileSize(t.FileSize));
                    lvi.SubItems.Add(t.AgeDays > 0 ? $"{t.AgeDays} 天前" : "刚刚");
                    list.Items.Add(lvi);
                }
            }
            finally
            {
                list.EndUpdate();
            }

            list.Visible = data.Count > 0;
            emptyLabel.Visible = data.Count == 0;
        }

        RefreshList(items);

        // 底部按钮栏
        FlowLayoutPanel btnPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 48,
            Padding = new Padding(8),
        };

        Button closeBtn = new Button { Text = "关闭", Width = 88, Height = CloudPanSpacing.MinTouchSize, FlatStyle = FlatStyle.Flat };
        closeBtn.FlatAppearance.BorderColor = CloudPanColors.ButtonBorderGray;
        void OnCloseClick(object? s, EventArgs e) => dialog.Close();
        closeBtn.Click += OnCloseClick;

        Button emptyBtn = new Button
        {
            Text = "清空回收站",
            Width = 120,
            Height = CloudPanSpacing.MinTouchSize,
            FlatStyle = FlatStyle.Flat,
            Margin = new Padding(0, 0, 8, 0),
            UseVisualStyleBackColor = false,
            BackColor = CloudPanColors.ErrorBgLight,
            ForeColor = CloudPanColors.TextError,
        };
        emptyBtn.FlatAppearance.BorderColor = CloudPanColors.ErrorRed;
        async void OnEmptyClick(object? s, EventArgs e)
        {
            try
            {
                if (MessageBox.Show(dialog, "确定要清空回收站吗？清空后无法恢复。", "清空回收站",
                        MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK)
                {
                    return;
                }

                bool ok = await _engine.EmptyTrashAsync();
                AddLog(ok ? "已清空回收站" : "清空回收站失败");
                var refreshed = await _engine.GetTrashAsync();
                RefreshList(refreshed);
                if (refreshed.Count == 0)
                {
                    dialog.Close();
                }
            }
            catch (Exception ex)
            {
                AddLog($"清空回收站失败: {ex.Message}");
            }
        }
        emptyBtn.Click += OnEmptyClick;

        Button restoreBtn = new Button
        {
            Text = "恢复选中",
            Width = 100,
            Height = CloudPanSpacing.MinTouchSize,
            FlatStyle = FlatStyle.Flat,
            Margin = new Padding(0, 0, 8, 0),
            UseVisualStyleBackColor = false,
            BackColor = CloudPanColors.SuccessBgLight,
        };
        restoreBtn.FlatAppearance.BorderColor = CloudPanColors.SuccessGreen;
        async void OnRestoreClick(object? s, EventArgs e)
        {
            try
            {
                if (list.SelectedItems.Count == 0 || list.SelectedItems[0].Tag is not TrashItem item)
                {
                    return;
                }

                bool ok = await _engine.RestoreTrashAsync(item);
                AddLog(ok ? $"已恢复: {item.OriginalPath}" : $"恢复失败: {item.OriginalPath}");
                var refreshed = await _engine.GetTrashAsync();
                RefreshList(refreshed);
                if (refreshed.Count == 0)
                {
                    dialog.Close();
                }
            }
            catch (Exception ex)
            {
                AddLog($"恢复失败: {ex.Message}");
            }
        }
        restoreBtn.Click += OnRestoreClick;

        btnPanel.Controls.Add(closeBtn);
        btnPanel.Controls.Add(emptyBtn);
        btnPanel.Controls.Add(restoreBtn);

        dialog.Controls.Add(list);
        dialog.Controls.Add(emptyLabel);
        dialog.Controls.Add(btnPanel);
        dialog.ShowDialog(this);
    }
}
