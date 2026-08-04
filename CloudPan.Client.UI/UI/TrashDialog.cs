using System.Drawing;
using System.Windows.Forms;
using CloudPan.Client.Core.Services;
using CloudPan.Contract;
using CloudPan.Infrastructure.Design;

namespace CloudPan.Client.UI;

/// <summary>
/// 回收站对话框（T-099 从 MainWindow 下沉为独立 internal 类，以满足 MainWindow 聚合行数门禁）：
/// 列出回收站条目，支持恢复选中 / 清空回收站（复用 /api/trash 三端点）。
/// </summary>
internal static class TrashDialog
{
    /// <summary>最近删除对话框：列出回收站条目，支持恢复选中 / 清空回收站（复用 /api/trash 三端点）。</summary>
    public static void Show(IWin32Window owner, SyncEngine engine, Action<string> addLog, List<TrashItem> items)
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

                bool ok = await engine.EmptyTrashAsync();
                addLog(ok ? "已清空回收站" : "清空回收站失败");
                if (!ok)
                {
                    // T-115：主动清空失败弹可见提示（服务端异常已吞为 false，给通用白话下一步），不再只写默认折叠的日志栏
                    MessageBox.Show(dialog, "清空回收站失败，请检查网络连接后重试。", "清空回收站",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                var refreshed = await engine.GetTrashAsync();
                RefreshList(refreshed);
                if (refreshed.Count == 0)
                {
                    dialog.Close();
                }
            }
            catch (Exception ex)
            {
                // T-115：主动清空失败弹白话提示（原因+下一步），不再只写默认折叠的日志栏
                ErrorAttribution attribution = ErrorAttribution.FromException(ex);
                addLog($"清空回收站失败: {ex.Message}");
                MessageBox.Show(dialog, $"清空回收站失败：{attribution.Message}。{attribution.NextStep}", "清空回收站",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
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

                // T-094/F-136：恢复失败不再静默 AddLog——冲突弹白话原因+覆盖/改名选项，其余失败也弹可见提示
                bool ok = await RestoreConflictDialog.RestoreAsync(engine, addLog, item, dialog);
                if (ok)
                {
                    addLog($"已恢复: {item.OriginalPath}");
                }
                var refreshed = await engine.GetTrashAsync();
                RefreshList(refreshed);
                if (refreshed.Count == 0)
                {
                    dialog.Close();
                }
            }
            catch (Exception ex)
            {
                addLog($"恢复失败: {ex.Message}");
                MessageBox.Show(dialog, $"恢复失败：{ex.Message}", "恢复失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        restoreBtn.Click += OnRestoreClick;

        btnPanel.Controls.Add(closeBtn);
        btnPanel.Controls.Add(emptyBtn);
        btnPanel.Controls.Add(restoreBtn);

        dialog.Controls.Add(list);
        dialog.Controls.Add(emptyLabel);
        dialog.Controls.Add(btnPanel);
        dialog.ShowDialog(owner);
    }
}
