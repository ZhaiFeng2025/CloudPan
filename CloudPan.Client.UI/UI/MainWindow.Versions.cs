using System.Diagnostics;
using System.Drawing.Drawing2D;
using CloudPan.Client.Core.Models;
using CloudPan.Client.Core.Services;
using CloudPan.Contract;
using CloudPan.Infrastructure.Design;

namespace CloudPan.Client.UI;

/// <summary>MainWindow 部分类：版本历史对话框与入口、时间戳格式化。</summary>
public partial class MainWindow
{

    // ================================================================
    // 版本历史（T-018）
    // ================================================================

    /// <summary>T-018：文件浏览「版本」→ 打开版本历史对话框（列表 + 回滚）。</summary>
    private async void FileBrowser_VersionHistoryRequested(FileBrowseItem item)
    {
        try
        {
            await ShowVersionHistoryDialogAsync(item);
        }
        catch (Exception ex)
        {
            AddLog($"打开版本历史失败: {ex.Message}");
        }
    }

    /// <summary>T-018：版本历史对话框：列出历史版本并可回滚（回滚先存档当前版本，再用历史文件覆盖）。</summary>
    private async Task ShowVersionHistoryDialogAsync(FileBrowseItem item)
    {
        var baseFont = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;
        List<VersionItem> versions = await _engine.GetVersionHistoryAsync(item.Path);

        Form dialog = new Form
        {
            Text = "版本历史",
            Size = new Size(640, 420),
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            ShowInTaskbar = false,
        };

        Label titleLabel = new Label
        {
            Text = $"版本历史：{item.Path}",
            Dock = DockStyle.Top,
            Height = 40,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(12, 0, 0, 0),
            Font = new Font(baseFont.FontFamily, CloudPanFonts.SizeBody, FontStyle.Bold),
            ForeColor = CloudPanColors.TextPrimary,
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
        list.Columns.Add("版本", 70);
        list.Columns.Add("大小", 90);
        list.Columns.Add("修改时间", 170);
        list.Columns.Add("设备", 120);

        Label emptyLabel = new Label
        {
            Dock = DockStyle.Fill,
            Text = "暂无历史版本\n（文件每次同步更新都会保留一个历史版本）",
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = CloudPanColors.TextMuted,
            Font = new Font(baseFont.FontFamily, CloudPanFonts.SizeBody),
            BackColor = CloudPanColors.BackgroundWhite,
            Visible = versions.Count == 0,
        };

        void RefreshList(List<VersionItem> data)
        {
            list.BeginUpdate();
            try
            {
                list.Items.Clear();
                foreach (VersionItem v in data)
                {
                    ListViewItem lvi = new ListViewItem($"v{v.Version}") { Tag = v };
                    lvi.SubItems.Add(FormatFileSize(v.Size));
                    lvi.SubItems.Add(FormatTimestamp(v.Timestamp));
                    lvi.SubItems.Add(v.DeviceId);
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

        RefreshList(versions);

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

        Button restoreBtn = new Button
        {
            Text = "回滚到选中版本",
            Width = 140,
            Height = CloudPanSpacing.MinTouchSize,
            FlatStyle = FlatStyle.Flat,
            Margin = new Padding(0, 0, 8, 0),
            UseVisualStyleBackColor = false,
            BackColor = CloudPanColors.SuccessBgLight,
            Enabled = versions.Count > 0,
        };
        restoreBtn.FlatAppearance.BorderColor = CloudPanColors.SuccessGreen;
        async void OnRestoreClick(object? s, EventArgs e)
        {
            try
            {
                if (list.SelectedItems.Count == 0 || list.SelectedItems[0].Tag is not VersionItem v)
                {
                    MessageBox.Show(dialog, "请先选中要回滚的历史版本。", "版本历史",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                if (MessageBox.Show(dialog,
                        $"确定将 “{item.Name}” 回滚到 v{v.Version} 吗？\n当前版本会先存档为新的历史版本。",
                        "版本回滚", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK)
                {
                    return;
                }

                var result = await _engine.RestoreVersionAsync(item.Path, v.Version);
                if (result?.Data == null)
                {
                    MessageBox.Show(dialog, "回滚失败，请检查服务端连接后重试。", "版本历史",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                MessageBox.Show(dialog, $"已回滚到 v{v.Version}。当前内容已更新，其他设备将自动同步。",
                    "版本历史", MessageBoxButtons.OK, MessageBoxIcon.Information);
                AddLog($"已回滚 {item.Path} 到 v{v.Version}");
                dialog.Close();
                await LoadBrowserAsync(); // 回滚后刷新文件浏览（大小/状态可能变化）
            }
            catch (Exception ex)
            {
                MessageBox.Show(dialog, $"回滚失败: {ex.Message}", "版本历史",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        restoreBtn.Click += OnRestoreClick;

        btnPanel.Controls.Add(closeBtn);
        btnPanel.Controls.Add(restoreBtn);

        dialog.Controls.Add(list);
        dialog.Controls.Add(emptyLabel);
        dialog.Controls.Add(titleLabel);
        dialog.Controls.Add(btnPanel);
        dialog.ShowDialog(this);
    }

    /// <summary>T-018：托盘「版本历史」入口——显示窗口并对当前选中文件打开版本历史对话框。</summary>
    public async void OpenVersionHistoryForSelection()
    {
        try
        {
            var item = _fileBrowser.SelectedItem;
            if (item == null || item.IsDirectory)
            {
                AddLog("请先在文件浏览中选中一个文件，再查看版本历史");
                return;
            }

            await ShowVersionHistoryDialogAsync(item);
        }
        catch (Exception ex)
        {
            AddLog($"打开版本历史失败: {ex.Message}");
        }
    }

    /// <summary>将 ISO 8601 时间字符串格式化为本地可读时间（yyyy-MM-dd HH:mm）。</summary>
    private static string FormatTimestamp(string timestamp)
    {
        if (DateTime.TryParse(timestamp, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal, out DateTime utc))
        {
            return utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        }

        return timestamp;
    }
}
