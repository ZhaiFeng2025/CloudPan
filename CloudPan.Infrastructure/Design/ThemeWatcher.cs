namespace CloudPan.Infrastructure.Design;

/// <summary>
/// 主题跟随器（T-032 深色模式令牌级切换；T-079 两端收敛至此单一实现）。
/// 窗口经 <see cref="Watch"/> 一行接入：立即把控件树颜色归一化到当前系统主题，
/// 并在系统主题切换（<see cref="ThemeManager.ThemeChanged"/>）时刷新。颜色映射在令牌层
/// CloudPanColors.NormalizeToTheme/NormalizeTextToTheme 内完成，UI 不做逐处反色。
/// 客户端与服务端 UI 共用本实现，改策略只改一处，两端深色模式行为不分叉。
/// </summary>
public static class ThemeWatcher
{
    private static readonly object Gate = new();
    private static readonly List<Form> Forms = new();
    private static bool _subscribed;

    /// <summary>窗口接入主题跟随：立即应用当前主题，并注册系统主题切换刷新。可重复调用（幂等）。</summary>
    public static void Watch(Form form)
    {
        if (form == null || form.IsDisposed)
        {
            return;
        }

        lock (Gate)
        {
            EnsureSubscribed();
            ApplyTo(form);
            Forms.Add(form);
            form.FormClosed += OnFormClosed;
        }
    }

    /// <summary>窗口关闭后从跟随列表移除（具名处理，可在 Dispose 前退订）。</summary>
    private static void OnFormClosed(object? sender, EventArgs e)
    {
        if (sender is Form form)
        {
            lock (Gate)
            {
                Forms.Remove(form);
            }
        }
    }

    /// <summary>把控件树颜色归一化到当前主题（供 Watch 与主题切换刷新使用，幂等）。</summary>
    public static void ApplyTo(Control root)
    {
        if (root == null || root.IsDisposed)
        {
            return;
        }

        ApplyToTree(root);
    }

    private static void EnsureSubscribed()
    {
        if (_subscribed)
        {
            return;
        }

        _subscribed = true;
        ThemeManager.Initialize();
        ThemeManager.ThemeChanged += OnThemeChanged;
    }

    /// <summary>系统主题切换：把跟随中的窗口颜色归一化到新主题（具名处理）。</summary>
    private static void OnThemeChanged(object? sender, EventArgs e) => RefreshAll();

    private static void RefreshAll()
    {
        Form[] snapshot;
        lock (Gate)
        {
            snapshot = Forms.ToArray();
        }

        foreach (var form in snapshot)
        {
            try
            {
                if (form.IsDisposed)
                {
                    continue;
                }

                // ThemeChanged 在 SystemEvents 回调线程触发，须回 UI 线程再改控件（CLAUDE.md 7.4）
                form.BeginInvoke(new Action(() => ApplyTo(form)));
            }
            catch
            {
                // 窗口句柄不可用（隐藏到托盘等）时忽略，下次 Watch/显示时归一化
            }
        }
    }

    private static void ApplyToTree(Control control)
    {
        if (control.IsDisposed)
        {
            return;
        }

        try
        {
            control.BackColor = CloudPanColors.NormalizeToTheme(control.BackColor);
            control.ForeColor = CloudPanColors.NormalizeTextToTheme(control.ForeColor);

            // ListView 项/子项颜色不在 Controls 树内，需单独归一化
            if (control is ListView listView)
            {
                foreach (ListViewItem item in listView.Items)
                {
                    item.BackColor = CloudPanColors.NormalizeToTheme(item.BackColor);
                    item.ForeColor = CloudPanColors.NormalizeTextToTheme(item.ForeColor);
                    foreach (ListViewItem.ListViewSubItem subItem in item.SubItems)
                    {
                        subItem.BackColor = CloudPanColors.NormalizeToTheme(subItem.BackColor);
                        subItem.ForeColor = CloudPanColors.NormalizeTextToTheme(subItem.ForeColor);
                    }
                }
            }

            // Button FlatAppearance（悬停/按下/边框色）随主题归一化
            if (control is Button button && button.FlatStyle == FlatStyle.Flat)
            {
                button.FlatAppearance.BorderColor = CloudPanColors.NormalizeToTheme(button.FlatAppearance.BorderColor);
                button.FlatAppearance.MouseOverBackColor = CloudPanColors.NormalizeToTheme(button.FlatAppearance.MouseOverBackColor);
                button.FlatAppearance.MouseDownBackColor = CloudPanColors.NormalizeToTheme(button.FlatAppearance.MouseDownBackColor);
            }
        }
        catch
        {
            // 单个控件设置失败不阻断整树归一化
        }

        foreach (Control child in control.Controls)
        {
            ApplyToTree(child);
        }
    }
}
