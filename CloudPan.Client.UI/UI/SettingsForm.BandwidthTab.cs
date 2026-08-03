using CloudPan.Infrastructure.Design;

namespace CloudPan.Client.UI;

/// <summary>SettingsForm 部分类：带宽限制 Tab 页（上下行限速输入 + 预设按钮 + 数字输入过滤）。</summary>
public partial class SettingsForm
{
    // ──────────────────────────────────────────────
    // Tab 2: 带宽限制（含预设按钮）
    // ──────────────────────────────────────────────

    private void BuildBandwidthTab(long uploadSpeedBps, long downloadSpeedBps)
    {
        TabPage bwTab = new TabPage("带宽限制");
        FlowLayoutPanel bwPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16),
            FlowDirection = FlowDirection.TopDown,
        };

        // 上传限速
        bwPanel.Controls.Add(new Label
        {
            Text = "上传限速 (KB/s，0=不限速)",
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 2),
        });
        _uploadLimitBox = new TextBox
        {
            Text = (uploadSpeedBps / 1024).ToString(),
            Width = 120,
        };
        _uploadLimitBox.KeyPress += NumericOnly_KeyPress;
        bwPanel.Controls.Add(_uploadLimitBox);

        // 下载限速
        bwPanel.Controls.Add(new Label
        {
            Text = "下载限速 (KB/s，0=不限速)",
            AutoSize = true,
            Margin = new Padding(0, 10, 0, 2),
        });
        _downloadLimitBox = new TextBox
        {
            Text = (downloadSpeedBps / 1024).ToString(),
            Width = 120,
        };
        _downloadLimitBox.KeyPress += NumericOnly_KeyPress;
        bwPanel.Controls.Add(_downloadLimitBox);

        // 预设按钮
        bwPanel.Controls.Add(new Label
        {
            Text = "快捷设置",
            AutoSize = true,
            Margin = new Padding(0, 12, 0, 4),
            Font = new Font(CloudPanFonts.FontFamily, 9F, FontStyle.Bold),
        });

        FlowLayoutPanel presetRow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };

        (string text, int kbValue)[] presets = new (string text, int kbValue)[]
        {
            ("不限速", 0),
            ("1MB/s", 1024),
            ("5MB/s", 5120),
            ("10MB/s", 10240),
        };

        foreach (var (text, kbValue) in presets)
        {
            Button btn = new Button
            {
                Text = text,
                Width = 72,
                Height = 26,
                FlatStyle = FlatStyle.Flat,
                BackColor = CloudPanColors.BackgroundLight,
                ForeColor = CloudPanColors.TextSecondary,
                Font = new Font(CloudPanFonts.FontFamily, CloudPanFonts.SizeBody),
                Cursor = Cursors.Hand,
                UseVisualStyleBackColor = false,
            };
            btn.FlatAppearance.BorderColor = CloudPanColors.BorderLight;
            btn.FlatAppearance.MouseOverBackColor = CloudPanColors.ButtonHoverBg;
            btn.FlatAppearance.MouseDownBackColor = CloudPanColors.ButtonPressBg;

            // 值经 Tag 传递到具名处理器（CP301：避免捕获循环变量的匿名 lambda）
            btn.Tag = kbValue;
            btn.Click += PresetBtn_Click;

            presetRow.Controls.Add(btn);
        }

        bwPanel.Controls.Add(presetRow);
        bwTab.Controls.Add(bwPanel);
        _tabs.TabPages.Add(bwTab);
    }

    // ──────────────────────────────────────────────
    // 具名事件处理器（CP301：避免匿名 lambda 订阅无法退订）
    // ──────────────────────────────────────────────

    /// <summary>预设限速按钮：把按钮 Tag 中的值（KB/s）应用到上下行输入框。</summary>
    private void PresetBtn_Click(object? sender, EventArgs e)
    {
        if (sender is Button btn && btn.Tag is int value)
        {
            _uploadLimitBox.Text = value.ToString();
            _downloadLimitBox.Text = value.ToString();
        }
    }

    /// <summary>只允许数字输入，退格和方向键除外。</summary>
    private static void NumericOnly_KeyPress(object? sender, KeyPressEventArgs e)
    {
        if (!char.IsControl(e.KeyChar) && !char.IsDigit(e.KeyChar))
        {
            e.Handled = true;
        }
    }
}
