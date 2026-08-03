using System.Drawing.Drawing2D;
using CloudPan.Infrastructure.Design;

namespace CloudPan.Server.UI;

/// <summary>ServerWindow 部分类：窗口生命周期事件、空状态布局与统计卡片（圆角）绘制。</summary>
public partial class ServerWindow
{
    // ===== 具名事件处理器（CP301：避免匿名 lambda 订阅无法退订） =====

    private void EmptyStatePanel_Resize(object? sender, EventArgs e) => CenterEmptyState();

    private void ClearLogBtn_Click(object? sender, EventArgs e)
    {
        _logList.Items.Clear();
        AddLog("日志已清空");
    }

    private async void RefreshTimer_Tick(object? sender, EventArgs e) => await RefreshDataAsync();

    /// <summary>关闭按钮 → 隐藏到托盘（而非销毁窗口）。系统/任务管理器关闭时放行。</summary>
    private void Window_FormClosing(object? sender, FormClosingEventArgs e)
    {
        // Application.Exit() / 进程退出 → 允许关闭
        if (e.CloseReason == CloseReason.ApplicationExitCall
            || e.CloseReason == CloseReason.TaskManagerClosing
            || e.CloseReason == CloseReason.WindowsShutDown)
        {
            _refreshTimer.Stop();
            return;
        }
        // 用户点击 X 按钮 → 隐藏到托盘
        e.Cancel = true;
        Hide();
        AddLog("窗口已隐藏至系统托盘，左键托盘图标可重新打开");
    }

    /// <summary>最小化时 → 隐藏到托盘（服务端窗口不应占据任务栏）。</summary>
    private void Window_Resize(object? sender, EventArgs e)
    {
        if (WindowState == FormWindowState.Minimized)
        {
            Hide();
            AddLog("窗口已最小化至系统托盘");
        }
    }

    /// <summary>首次显示时：刷新数据 + 刷入缓存日志。</summary>
    private async void Window_Shown(object? sender, EventArgs e)
    {
        FlushPendingLogs();
        await RefreshDataAsync();
    }

    /// <summary>
    /// 将窗口句柄创建前缓存的消息刷入日志列表。
    /// </summary>
    private void FlushPendingLogs()
    {
        if (_pendingLogs.Count == 0) return;
        foreach (string msg in _pendingLogs)
        {
            _logList.Items.Add($"[{DateTime.Now:HH:mm:ss}] {msg}");
        }
        _pendingLogs.Clear();
    }

    /// <summary>
    /// 居中空状态面板内的图标和文字
    /// </summary>
    private void CenterEmptyState()
    {
        if (_emptyStatePanel.Width <= 0 || _emptyStatePanel.Height <= 0)
        {
            return;
        }

        int cx = _emptyStatePanel.Width / 2;
        int cy = _emptyStatePanel.Height / 2;
        _emptyIcon.Location = new Point(cx - _emptyIcon.Width / 2, cy - 70);
        _emptyTitle.Location = new Point(cx - _emptyTitle.Width / 2, cy - 10);
        _emptyHint.Location = new Point(cx - _emptyHint.Width / 2, cy + 20);
    }

    /// <summary>
    /// 为控件设置圆角区域
    /// </summary>
    private static void SetRoundedRegion(Control ctrl, int radius)
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
    private static Label CreateStatCard(TableLayoutPanel parent, string title, string value, int col)
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
