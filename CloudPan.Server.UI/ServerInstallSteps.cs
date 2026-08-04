using System.Drawing.Drawing2D;
using CloudPan.Infrastructure.Design;

namespace CloudPan.Server.UI;

/// <summary>安装向导步骤指示器协作类（T-110）：步骤绘图、状态文字与 Token 成功闪烁动画。逻辑从 ServerInstaller 外提。</summary>
internal sealed class ServerInstallSteps
{
    private readonly ServerInstaller _form;

    public ServerInstallSteps(ServerInstaller form)
    {
        _form = form;
    }

    // =================================================================
    //  步骤指示器绘图
    // =================================================================
    internal void StepPanel_Paint(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        string[] steps = new[] { "清理", "安装", "启动", "防火墙", "就绪" };
        int w = _form._stepPanel.Width;
        int startX = 30;
        int endX = w - 30;
        int stepW = endX - startX > 0 ? (endX - startX) / (steps.Length - 1) : 60;
        int cy = 11;  // 圆心 Y
        int r = 10;   // 半径
        int d = r * 2;

        for (int i = 0; i < steps.Length; i++)
        {
            int cx = startX + i * stepW;

            bool completed = _form._currentStep > i;
            bool current = _form._currentStep == i;

            Color circleColor, textColor;
            bool filled;

            if (completed || _form._currentStep >= steps.Length)
            {
                circleColor = CloudPanColors.SuccessGreen;
                textColor = CloudPanColors.SuccessGreen;
                filled = true;
            }
            else if (current)
            {
                circleColor = CloudPanColors.PrimaryBlue;
                textColor = CloudPanColors.PrimaryBlue;
                filled = true;
            }
            else
            {
                circleColor = CloudPanColors.BorderMid;
                textColor = CloudPanColors.TextMuted;
                filled = false;
            }

            // 连接线（到前一个步骤）
            if (i > 0)
            {
                int prevCx = startX + (i - 1) * stepW;
                // 前序步骤（i-1）已完成 = 连接线绿色；前序步骤是当前步骤 = 蓝色
                bool prevDone = _form._currentStep > i - 1 || _form._currentStep >= steps.Length;
                bool prevCurrent = _form._currentStep == i - 1;
                Color lineColor;
                if (prevDone)
                {
                    lineColor = CloudPanColors.SuccessGreen;
                }
                else if (prevCurrent)
                {
                    lineColor = CloudPanColors.PrimaryBlue;
                }
                else
                {
                    lineColor = CloudPanColors.BorderLight;
                }

                using Pen linePen = new Pen(lineColor, 2.5f);
                g.DrawLine(linePen, prevCx + r, cy, cx - r, cy);
            }

            // 圆
            if (filled)
            {
                using SolidBrush brush = new SolidBrush(circleColor);
                g.FillEllipse(brush, cx - r, cy - r, d, d);
            }
            else
            {
                using Pen pen = new Pen(circleColor, 2f);
                g.DrawEllipse(pen, cx - r, cy - r, d, d);
            }

            // 步骤编号
            using Font numFont = new Font(CloudPanFonts.FontFamily, 8f, FontStyle.Bold);
            string numText = (i + 1).ToString();
            var numSize = g.MeasureString(numText, numFont);
            using SolidBrush numBrush = new SolidBrush(filled ? CloudPanColors.TextOnPrimary : circleColor);
            g.DrawString(numText, numFont, numBrush,
                cx - numSize.Width / 2, cy - numSize.Height / 2);

            // 步骤标签
            using Font labelFont = new Font(CloudPanFonts.FontFamily, 7.5f,
                current ? FontStyle.Bold : FontStyle.Regular);
            var labelSize = g.MeasureString(steps[i], labelFont);
            using SolidBrush labelBrush = new SolidBrush(textColor);
            g.DrawString(steps[i], labelFont, labelBrush,
                cx - labelSize.Width / 2, cy + r + 3);
        }
    }

    /// <summary>
    /// 切换到指定步骤（0‑based），更新进度条并重绘步骤指示器
    /// </summary>
    internal void SetStep(int stepIndex)
    {
        _form._currentStep = stepIndex;
        // 进度条最大值为 5（5 步：清理/安装/启动/防火墙/就绪），步骤 0-4 各占 20%
        _form._progressBar.Value = Math.Clamp(stepIndex + 1, 0, _form._progressBar.Maximum);
        _form._stepPanel.Invalidate();
    }

    /// <summary>
    /// 设置状态文字并自适应高度
    /// </summary>
    internal void SetStatusText(string text)
    {
        _form._statusLabel.Text = text;
        int textWidth = _form._statusLabel.Width - _form._statusLabel.Padding.Horizontal;
        if (textWidth > 0)
        {
            var size = TextRenderer.MeasureText(text, _form._statusLabel.Font,
                new Size(textWidth, 0), TextFormatFlags.WordBreak);
            _form._statusLabel.Height = size.Height + _form._statusLabel.Padding.Vertical + 8;
        }
    }

    /// <summary>
    /// Token 显示成功动画：绿色边框闪烁 3 次后稳定
    /// </summary>
    internal void FlashSuccessBorder()
    {
        _form._flashOriginalColor = _form._tokenBorder.BackColor;
        _form._flashColor = CloudPanColors.SuccessGreen;
        _form._flashCount = 0;
        System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = CloudPanEffects.DurationNormal };
        timer.Tick += FlashTimer_Tick;
        timer.Start();
    }

    /// <summary>闪烁动画 Timer 回调：交替显示原色/绿色，3 次后停在绿色并释放 Timer。</summary>
    private void FlashTimer_Tick(object? sender, EventArgs e)
    {
        _form._flashCount++;
        _form._tokenBorder.BackColor = _form._flashCount % 2 == 1 ? _form._flashColor : _form._flashOriginalColor;
        if (_form._flashCount >= 5) // 3 次闪烁后停在绿色
        {
            var timer = (System.Windows.Forms.Timer)sender!;
            timer.Stop();
            timer.Dispose();
            _form._tokenBorder.BackColor = _form._flashColor;
        }
    }
}
