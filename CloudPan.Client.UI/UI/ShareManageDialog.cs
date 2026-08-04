using System.Drawing;
using System.Globalization;
using System.IO;
using System.Windows.Forms;
using CloudPan.Client.Core.Services;
using CloudPan.Contract;
using CloudPan.Infrastructure.Design;

namespace CloudPan.Client.UI;

/// <summary>
/// 管理分享对话框（T-112）：列出当前设备创建的历史分享链接，支持查看与撤销。
/// 服务端 GET /api/shares（新列表端点）提供数据，撤销复用 DELETE /api/shares/{shareId}。
/// 替代 ShareDialog 中「生成后即撤销」的受限能力——历史链接可随时查看/撤销。
/// </summary>
internal static class ShareManageDialog
{
    public static void Show(IWin32Window owner, SyncEngine engine, Action<string> addLog)
    {
        var baseFont = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;

        Form dialog = new Form
        {
            Text = "管理分享",
            Size = new Size(620, 460),
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            ShowInTaskbar = false,
        };

        TableLayoutPanel root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16, 12, 16, 12),
            BackColor = CloudPanColors.BackgroundWhite,
        };
        root.ColumnCount = 1;
        root.RowCount = 4;
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 30)); // 提示行
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // 列表
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42)); // 按钮行
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 30)); // 状态行

        Label hintLabel = new Label
        {
            Text = "以下为当前设备创建的历史分享链接，可随时撤销（撤销后链接立即失效）：",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font(baseFont.FontFamily, CloudPanFonts.SizeBody),
            ForeColor = CloudPanColors.TextPrimary,
        };
        root.Controls.Add(hintLabel, 0, 0);

        ListView list = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = false,
            HideSelection = false,
            Font = new Font(baseFont.FontFamily, CloudPanFonts.SizeBody),
        };
        list.Columns.Add("文件", 200);
        list.Columns.Add("过期时间", 140);
        list.Columns.Add("下载", 80);
        list.Columns.Add("创建时间", 140);
        root.Controls.Add(list, 0, 1);

        FlowLayoutPanel btnRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            Margin = new Padding(0, 6, 0, 0),
        };
        Button revokeBtn = new Button
        {
            Text = "撤销选中",
            Width = 100,
            Height = CloudPanSpacing.MinTouchSize,
            FlatStyle = FlatStyle.Flat,
            UseVisualStyleBackColor = false,
            BackColor = CloudPanColors.ErrorBgLight,
            ForeColor = CloudPanColors.TextError,
        };
        revokeBtn.FlatAppearance.BorderColor = CloudPanColors.ErrorRed;
        Button refreshBtn = new Button
        {
            Text = "刷新",
            Width = 80,
            Height = CloudPanSpacing.MinTouchSize,
            FlatStyle = FlatStyle.Flat,
            Margin = new Padding(0, 0, 8, 0),
        };
        refreshBtn.FlatAppearance.BorderColor = CloudPanColors.ButtonBorderGray;
        btnRow.Controls.Add(revokeBtn); // RightToLeft：先添加在右
        btnRow.Controls.Add(refreshBtn);
        root.Controls.Add(btnRow, 0, 2);

        Label statusLabel = new Label
        {
            Dock = DockStyle.Fill,
            Text = "正在加载分享列表…",
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font(baseFont.FontFamily, CloudPanFonts.SizeBody),
            ForeColor = CloudPanColors.TextMuted,
        };
        root.Controls.Add(statusLabel, 0, 3);

        // 加载列表（async void：UI 事件处理器 + 顶层 try-catch，CLAUDE.md §7.2）
        async void Load()
        {
            refreshBtn.Enabled = false;
            list.Items.Clear();
            statusLabel.Text = "正在加载分享列表…";
            try
            {
                var shares = await engine.GetSharesAsync();
                foreach (var s in shares)
                {
                    ListViewItem item = new ListViewItem(Path.GetFileName(s.FilePath));
                    item.Tag = s;
                    item.SubItems.Add(FormatDateTime(s.ExpiresAt, "永不过期"));
                    item.SubItems.Add(s.MaxDownloads.HasValue
                        ? $"{s.UsedDownloads}/{s.MaxDownloads}"
                        : (s.UsedDownloads > 0 ? s.UsedDownloads.ToString() : "不限"));
                    item.SubItems.Add(FormatDateTime(s.CreatedAt, s.CreatedAt));
                    list.Items.Add(item);
                }

                statusLabel.Text = list.Items.Count == 0 ? "暂无分享链接" : $"共 {list.Items.Count} 个分享链接";
            }
            catch (Exception ex)
            {
                ErrorAttribution attribution = ErrorAttribution.FromException(ex);
                statusLabel.Text = $"加载失败：{attribution.Message}。{attribution.NextStep}";
            }
            finally
            {
                refreshBtn.Enabled = true;
            }
        }

        // 撤销选中（async void：UI 事件处理器 + 顶层 try-catch）
        async void OnRevoke(object? sender, EventArgs e)
        {
            if (list.SelectedItems.Count == 0)
            {
                statusLabel.Text = "请先选择要撤销的分享";
                return;
            }

            if (list.SelectedItems[0].Tag is not ShareListItem share)
            {
                return;
            }

            if (MessageBox.Show(dialog,
                    $"确定要撤销「{Path.GetFileName(share.FilePath)}」的分享链接吗？撤销后链接立即失效。",
                    "撤销分享", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK)
            {
                return;
            }

            try
            {
                bool ok = await engine.RevokeShareAsync(share.ShareId);
                statusLabel.Text = ok ? "已撤销分享，链接已失效" : "撤销失败（分享可能已失效）";
                addLog(ok ? $"已撤销分享: {share.FilePath}" : $"撤销分享失败: {share.FilePath}");
                Load(); // 撤销后刷新列表
            }
            catch (Exception ex)
            {
                ErrorAttribution attribution = ErrorAttribution.FromException(ex);
                statusLabel.Text = $"撤销失败：{attribution.Message}。{attribution.NextStep}";
            }
        }

        // 具名方法订阅（CP301：避免匿名 lambda 无法退订）
        void OnRefresh(object? sender, EventArgs e) => Load();
        void OnShown(object? sender, EventArgs e) => Load();
        revokeBtn.Click += OnRevoke;
        refreshBtn.Click += OnRefresh;
        dialog.Shown += OnShown;

        dialog.Controls.Add(root);
        dialog.ShowDialog(owner);
    }

    /// <summary>ISO 8601 UTC → 本地时间展示；null/解析失败回退默认文本。</summary>
    private static string FormatDateTime(string? iso, string fallback)
    {
        if (string.IsNullOrEmpty(iso)) return fallback;
        return DateTime.TryParse(iso, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal, out var dt)
            ? dt.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
            : fallback;
    }
}
