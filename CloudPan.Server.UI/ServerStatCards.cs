using System.Drawing.Drawing2D;
using CloudPan.Infrastructure.Design;

namespace CloudPan.Server.UI;

/// <summary>管理窗口统计卡片协作类（T-110）：圆角卡片区域与卡片标签构建。逻辑从 ServerWindow 外提。</summary>
internal static class ServerStatCards
{
    /// <summary>
    /// 为控件设置圆角区域
    /// </summary>
    internal static void SetRoundedRegion(Control ctrl, int radius)
    {
        if (ctrl.Width <= 0 || ctrl.Height <= 0)
        {
            return;
        }

        using GraphicsPath path = new GraphicsPath();
        int d = radius * 2;
        path.AddArc(0, 0, d, d, 180, 90);
        path.AddArc(ctrl.Width - d - 1, 0, d, d, 270, 90);
        path.AddArc(ctrl.Width - d - 1, ctrl.Height - d - 1, d, d, 0, 90);
        path.AddArc(0, ctrl.Height - d - 1, d, d, 90, 90);
        path.CloseFigure();
        // 先释放旧 Region 再设置新 Region，防止 Resize 高频触发时 GDI 句柄泄漏
        Region? oldRegion = ctrl.Region;
        ctrl.Region = new Region(path);
        oldRegion?.Dispose();
    }

    /// <summary>
    /// 创建带圆角背景的统计卡片
    /// </summary>
    internal static Label CreateStatCard(TableLayoutPanel parent, string title, string value, int col)
    {
        Panel card = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = CloudPanColors.BackgroundLight,
            Margin = new Padding(4),
            Padding = new Padding(8, 6, 8, 6)
        };
        void OnCardHandleCreated(object? s, EventArgs e)
            => SetRoundedRegion((Panel)s!, CloudPanEffects.CornerRadiusMd);
        void OnCardResize(object? s, EventArgs e)
        {
            var p = (Panel)s!;
            if (p.IsHandleCreated)
            {
                SetRoundedRegion(p, CloudPanEffects.CornerRadiusMd);
            }
        }
        card.HandleCreated += OnCardHandleCreated;
        card.Resize += OnCardResize;

        Label titleLbl = new Label
        {
            Text = title,
            Font = new Font(CloudPanFonts.FontFamily, CloudPanFonts.SizeCaption),
            ForeColor = CloudPanColors.TextMuted,
            AutoSize = true,
            BackColor = Color.Transparent
        };
        Label val = new Label
        {
            Text = value,
            Font = new Font(CloudPanFonts.FontFamily, CloudPanFonts.SizeSubtitle, FontStyle.Bold),
            AutoSize = true,
            ForeColor = CloudPanColors.SuccessGreen,
            BackColor = Color.Transparent
        };

        void CenterStatLabels()
        {
            titleLbl.Location = new Point((card.Width - titleLbl.Width) / 2, 6);
            val.Location = new Point((card.Width - val.Width) / 2, 26);
        }

        void OnCenterStatLabels(object? s, EventArgs e) => CenterStatLabels();
        card.SizeChanged += OnCenterStatLabels;
        card.HandleCreated += OnCenterStatLabels;

        card.Controls.Add(titleLbl);
        card.Controls.Add(val);
        parent.Controls.Add(card, col, 0);

        return val;
    }
}
