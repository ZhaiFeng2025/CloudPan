using System.Diagnostics;
using System.Drawing.Drawing2D;
using CloudPan.Client.Core.Models;
using CloudPan.Client.Core.Services;
using CloudPan.Contract;
using CloudPan.Infrastructure.Design;

namespace CloudPan.Client.UI;

/// <summary>MainWindow 部分类：GDI+ 发光指示灯与带文字进度条自定义控件。</summary>
public partial class MainWindow
{

    // ================================================================
    // 自定义控件
    // ================================================================

    /// <summary>GDI+ 绘制的发光状态指示灯。替换 Region 裁剪方案，绘制带发光效果和镜面高光的圆形。</summary>
    private class GlowDot : Panel
    {
        public GlowDot()
        {
            Size = new Size(16, 16);
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.Clear(Parent?.BackColor ?? Color.Transparent);

            float cx = Width / 2f;
            float cy = Height / 2f;
            const float radius = 5f;

            // 外层发光（使用 PathGradientBrush 实现径向渐变发光）
            using (GraphicsPath glowPath = new GraphicsPath())
            {
                float glowR = radius + 4f;
                glowPath.AddEllipse(cx - glowR, cy - glowR, glowR * 2, glowR * 2);
                using PathGradientBrush glowBrush = new PathGradientBrush(glowPath)
                {
                    CenterColor = Color.FromArgb(100, BackColor),
                    SurroundColors = new[] { Color.Transparent }
                };
                e.Graphics.FillEllipse(glowBrush, cx - glowR - 1, cy - glowR - 1,
                                       (glowR + 1) * 2, (glowR + 1) * 2);
            }

            // 实心圆
            using (SolidBrush circleBrush = new SolidBrush(BackColor))
            {
                e.Graphics.FillEllipse(circleBrush, cx - radius, cy - radius,
                                       radius * 2, radius * 2);
            }

            // 镜面高光（左上角小椭圆，模拟光照）
            using (SolidBrush highlight = new SolidBrush(Color.FromArgb(120, Color.White)))
            {
                e.Graphics.FillEllipse(highlight, cx - radius * 0.5f, cy - radius * 0.5f,
                                       radius * 1.2f, radius * 0.7f);
            }
        }
    }

    /// <summary>带百分比文字的进度条——自绘控件，在进度条上方叠加居中百分比文字。</summary>
    private class ProgressBarWithText : Control
    {
        private int _minimum;
        private int _maximum = 100;
        private int _value;
        private string _percentageText = "";

        public int Minimum
        {
            get => _minimum;
            set { _minimum = value; Invalidate(); }
        }

        public int Maximum
        {
            get => _maximum;
            set { _maximum = Math.Max(value, 1); Invalidate(); }
        }

        public int Value
        {
            get => _value;
            set { _value = Math.Clamp(value, _minimum, _maximum); Invalidate(); }
        }

        public string PercentageText
        {
            get => _percentageText;
            set { _percentageText = value ?? ""; Invalidate(); }
        }

        public ProgressBarWithText()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                     ControlStyles.SupportsTransparentBackColor, true);
            Height = 23;
            BackColor = Color.Transparent;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var rect = ClientRectangle;
            if (rect.Width <= 0 || rect.Height <= 0)
            {
                return;
            }

            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

            // 绘制外边框（凹陷效果）
            ControlPaint.DrawBorder3D(e.Graphics, rect, Border3DStyle.SunkenOuter);
            Rectangle innerRect = new Rectangle(rect.X + 2, rect.Y + 2,
                                          rect.Width - 4, rect.Height - 4);
            if (innerRect.Width <= 0 || innerRect.Height <= 0)
            {
                return;
            }

            // 绘制背景
            using (SolidBrush bgBrush = new SolidBrush(CloudPanColors.BackgroundWhite))
            {
                e.Graphics.FillRectangle(bgBrush, innerRect);
            }

            // 绘制进度条
            if (_maximum > _minimum && _value > _minimum)
            {
                float ratio = (float)(_value - _minimum) / (_maximum - _minimum);
                int barWidth = (int)(innerRect.Width * ratio);
                if (barWidth > 0)
                {
                    Rectangle barRect = new Rectangle(innerRect.X, innerRect.Y,
                                                barWidth, innerRect.Height);
                    using LinearGradientBrush barBrush = new LinearGradientBrush(
                        barRect, CloudPanColors.PrimaryBlue,
                        CloudPanColors.AccentBlue, LinearGradientMode.Horizontal);
                    e.Graphics.FillRectangle(barBrush, barRect);
                }
            }

            // 绘制百分比文字（白色 + 阴影轮廓）
            if (!string.IsNullOrEmpty(_percentageText))
            {
                // 阴影
                var shadowRect = rect;
                shadowRect.Offset(1, 0);
                TextRenderer.DrawText(e.Graphics, _percentageText, Font, shadowRect,
                    Color.FromArgb(80, 0, 0, 0),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.NoPadding);

                // 白色文字
                TextRenderer.DrawText(e.Graphics, _percentageText, Font, rect,
                    Color.White,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.NoPadding);
            }
        }
    }
}
