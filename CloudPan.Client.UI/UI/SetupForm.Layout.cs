using System.Drawing.Drawing2D;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using CloudPan.Contract;
using CloudPan.Infrastructure.Design;

namespace CloudPan.Client.UI;

/// <summary>SetupForm 部分类：布局构建、辅助构建与 Header 绘制。</summary>
public partial class SetupForm
{

    // ════════════════════════════════════════════════════════════════
    //  布局构建
    // ════════════════════════════════════════════════════════════════

    /// <summary>构建内容区的垂直堆叠控件。</summary>
    /// <remarks>
    /// 使用 Dock.Top 堆叠，添加顺序即为视觉从上到下的顺序。
    /// （Dock 按逆 Z 序处理，最后添加的控件 Z 序最高、最先被 Dock → 顶部。）
    /// </remarks>
    private void BuildContentStack(Panel parent)
    {
        // 弹性填充（确保所有字段靠上，额外空间在底部留白）
        parent.Controls.Add(new Panel { Dock = DockStyle.Fill });

        // ── 状态行 ──
        FlowLayoutPanel statusRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Height = 28,
        };
        statusRow.Controls.Add(_progressBar);
        statusRow.Controls.Add(_statusLabel);
        parent.Controls.Add(statusRow);

        // Spacer
        parent.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 6 });

        // ── Token 提示（输入框下方常驻说明） ──
        parent.Controls.Add(_tokenHintLabel);

        // ── Token 错误标签（在输入行下方、提示上方） ──
        parent.Controls.Add(_tokenErrorLabel);

        // ── Token 输入行 ──
        parent.Controls.Add(BuildTokenInputRow());

        // ── Token 标签行 ──
        FlowLayoutPanel tokenLabelRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Height = 24,
        };
        tokenLabelRow.Controls.Add(new Label
        {
            Text = "家庭 Token",
            AutoSize = true,
            TextAlign = ContentAlignment.BottomLeft,
            ForeColor = CloudPanColors.TextSecondary,
            Font = new Font(CloudPanFonts.FontFamily, 9F, FontStyle.Bold),
        });
        parent.Controls.Add(tokenLabelRow);

        // Spacer
        parent.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 6 });

        // ── 文件夹错误标签 ──
        parent.Controls.Add(_folderErrorLabel);

        // ── 文件夹输入行 ──
        parent.Controls.Add(BuildInputRow(_syncRootBox, _browseButton));

        // ── 文件夹标签 ──
        parent.Controls.Add(new Label
        {
            Text = "同步文件夹",
            Dock = DockStyle.Top,
            AutoSize = true,
            TextAlign = ContentAlignment.BottomLeft,
            Padding = new Padding(0, 4, 0, 2),
            ForeColor = CloudPanColors.TextSecondary,
            Font = new Font(CloudPanFonts.FontFamily, 9F, FontStyle.Bold),
        });

        // Spacer
        parent.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 6 });

        // ── URL 错误标签 ──
        parent.Controls.Add(_urlErrorLabel);

        // ── URL 输入行（TextBox + 状态图标 + 搜索按钮） ──
        parent.Controls.Add(BuildUrlInputRow());

        // ── URL 标签 ──
        parent.Controls.Add(new Label
        {
            Text = "服务端地址",
            Dock = DockStyle.Top,
            AutoSize = true,
            TextAlign = ContentAlignment.BottomLeft,
            Padding = new Padding(0, 4, 0, 2),
            ForeColor = CloudPanColors.TextSecondary,
            Font = new Font(CloudPanFonts.FontFamily, 9F, FontStyle.Bold),
        });
    }

    /// <summary>URL 输入行：TextBox + 状态图标 + 搜索按钮。</summary>
    private Panel BuildUrlInputRow()
    {
        TableLayoutPanel row = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 34,
            ColumnCount = 3,
            RowCount = 1,
            Margin = new Padding(0),
        };
        row.ColumnStyles.Clear();
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 22F));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100F));

        _serverUrlBox.Dock = DockStyle.Fill;
        _searchButton.Dock = DockStyle.Fill;
        _searchButton.Margin = new Padding(6, 0, 0, 0);
        _urlStatusIcon.Margin = new Padding(4, 0, 0, 0);

        row.Controls.Add(_serverUrlBox, 0, 0);
        row.Controls.Add(_urlStatusIcon, 1, 0);
        row.Controls.Add(_searchButton, 2, 0);

        return row;
    }

    /// <summary>Token 输入行：TextBox + 显示/隐藏按钮。</summary>
    private Panel BuildTokenInputRow()
    {
        TableLayoutPanel row = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 34,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0),
        };
        row.ColumnStyles.Clear();
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 62F));

        _tokenBox.Dock = DockStyle.Fill;
        _tokenToggleBtn.Dock = DockStyle.Fill;
        _tokenToggleBtn.Margin = new Padding(6, 0, 0, 0);

        row.Controls.Add(_tokenBox, 0, 0);
        row.Controls.Add(_tokenToggleBtn, 1, 0);

        return row;
    }

    /// <summary>通用输入行：TextBox + Button。</summary>
    private static Panel BuildInputRow(TextBox textBox, Button button)
    {
        TableLayoutPanel row = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 34,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0),
        };
        row.ColumnStyles.Clear();
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, button.Width + 6));

        textBox.Dock = DockStyle.Fill;
        button.Dock = DockStyle.Fill;
        button.Margin = new Padding(6, 0, 0, 0);

        row.Controls.Add(textBox, 0, 0);
        row.Controls.Add(button, 1, 0);

        return row;
    }

    /// <summary>底部操作按钮行。内部创建 _okButton 和取消按钮。</summary>
    private Panel BuildButtonRow()
    {
        FlowLayoutPanel btnRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Height = 56,
            Padding = new Padding(FieldMargin, 0, FieldMargin, 12),
            BackColor = CloudPanColors.BackgroundWhite,
        };

        _okButton = new Button
        {
            Text = "连接服务器",
            Width = CloudPanSpacing.ButtonWidth,
            Height = CloudPanSpacing.InputHeight,
            FlatStyle = FlatStyle.Flat,
            BackColor = CloudPanColors.PrimaryBlue,
            ForeColor = CloudPanColors.TextOnPrimary,
            Font = new Font(CloudPanFonts.FontFamily, 10F, FontStyle.Bold),
            Cursor = Cursors.Hand,
            UseVisualStyleBackColor = false,
        };
        _okButton.FlatAppearance.BorderSize = 0;
        _okButton.FlatAppearance.MouseOverBackColor = CloudPanColors.PrimaryBlueHover;
        _okButton.FlatAppearance.MouseDownBackColor = CloudPanColors.PrimaryBluePress;
        _okButton.Click += OnOkClick;

        Button cancelBtn = new Button
        {
            Text = "退出",
            Width = 72,
            Height = 34,
            FlatStyle = FlatStyle.Flat,
            BackColor = CloudPanColors.BackgroundWhite,
            ForeColor = CloudPanColors.TextSecondary,
            Font = new Font(CloudPanFonts.FontFamily, 9F),
            Cursor = Cursors.Hand,
            Margin = new Padding(8, 0, 0, 0),
            UseVisualStyleBackColor = false,
        };
        cancelBtn.FlatAppearance.BorderColor = CloudPanColors.BorderLight;
        cancelBtn.FlatAppearance.MouseOverBackColor = CloudPanColors.ButtonHoverBg;
        cancelBtn.FlatAppearance.MouseDownBackColor = CloudPanColors.ButtonPressBg;
        cancelBtn.Click += CancelBtn_Click;

        btnRow.Controls.Add(_okButton);
        btnRow.Controls.Add(cancelBtn);

        // CancelButton 在构造函数中设置
        btnRow.Tag = cancelBtn;
        return btnRow;
    }

    // ════════════════════════════════════════════════════════════════
    //  辅助构建
    // ════════════════════════════════════════════════════════════════

    private static TextBox CreateTextBox(string text, string placeholder)
    {
        return new TextBox
        {
            Text = text,
            PlaceholderText = placeholder,
            Font = new Font("Consolas", 10F),
            ForeColor = CloudPanColors.TextPrimary,
            BackColor = CloudPanColors.BackgroundWhite,
            BorderStyle = BorderStyle.FixedSingle,
        };
    }

    private static Button CreateFlatButton(string text, int width)
    {
        Button btn = new Button
        {
            Text = text,
            Width = width,
            FlatStyle = FlatStyle.Flat,
            BackColor = CloudPanColors.BackgroundWhite,
            ForeColor = CloudPanColors.TextSecondary,
            Font = new Font(CloudPanFonts.FontFamily, 9F),
            Cursor = Cursors.Hand,
            TextAlign = ContentAlignment.MiddleCenter,
            UseVisualStyleBackColor = false,
        };
        btn.FlatAppearance.BorderColor = CloudPanColors.BorderLight;
        btn.FlatAppearance.MouseOverBackColor = CloudPanColors.ButtonHoverBg;
        btn.FlatAppearance.MouseDownBackColor = CloudPanColors.ButtonPressBg;
        return btn;
    }

    private static Label CreateFieldMessageLabel()
    {
        return new Label
        {
            Text = "",
            Dock = DockStyle.Top,
            AutoSize = true,
            Margin = new Padding(0, 1, 0, 4),
            ForeColor = CloudPanColors.TextDarkGray,
            Font = new Font(CloudPanFonts.FontFamily, CloudPanFonts.SizeCaption),
            Visible = false,
        };
    }

    // ════════════════════════════════════════════════════════════════
    //  Header 绘制（复用 CloudPanIcon，保证与托盘图标一致）
    // ════════════════════════════════════════════════════════════════

    private static void OnHeaderPaint(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        const int iconSize = 36;
        const int margin = 28;
        int iconY = (76 - iconSize) / 2;

        // 使用 CloudPanIcon 绘制蓝色圆形云朵图标（与系统托盘图标一致）
        using (var fullIcon = CloudPanIcon.Create())
        using (Icon icon = new Icon(fullIcon, iconSize, iconSize))
        {
            g.DrawIcon(icon, margin, iconY);
        }

        // 标题
        int textX = margin + iconSize + 14;
        using (Font tf = new Font(CloudPanFonts.FontFamily, CloudPanFonts.SizeSubtitle, FontStyle.Bold))
        using (SolidBrush tb = new SolidBrush(CloudPanColors.TextPrimary))
        {
            g.DrawString("CloudPan 文件同步", tf, tb, textX, iconY + 1);
        }

        // 副标题
        using (Font sf = new Font(CloudPanFonts.FontFamily, 9F))
        using (SolidBrush sb = new SolidBrush(CloudPanColors.TextMuted))
        {
            g.DrawString("连接家庭文件同步服务端", sf, sb, textX, iconY + 27);
        }
    }
}
