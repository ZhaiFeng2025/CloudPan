using Microsoft.Win32;

namespace CloudPan.Infrastructure.Design;

/// <summary>
/// 系统主题管理器：跟随 Windows 应用主题（浅色/深色）在令牌层整体切换。
/// 初始值与运行时变更均从注册表 AppsUseLightTheme 读取（0=深色，1=浅色）；
/// SystemEvents.UserPreferenceChanged 监听系统主题变化。
/// 设计令牌 CloudPanColors 经 <see cref="IsDark"/> 在浅/深两组间整体解析，消费处自动生效，
/// 不要求 UI 逐处硬编码反色（visual-design-kb §6：令牌级切换）。
/// </summary>
public static class ThemeManager
{
    private static readonly object Gate = new();
    private static volatile bool _isDark;
    private static bool _initialized;

    /// <summary>当前是否深色主题。</summary>
    public static bool IsDark => _isDark;

    /// <summary>
    /// 系统主题切换事件。触发线程为 SystemEvents 回调线程（非 UI 线程），
    /// UI 消费方需 Invoke 回 UI 线程后再应用令牌颜色。
    /// </summary>
    public static event EventHandler? ThemeChanged;

    /// <summary>
    /// 在 UI 线程显式初始化（订阅系统主题事件）。幂等，可重复调用。
    /// WinForms 两端应在入口/窗体构造时调用一次，确保 SystemEvents 在有消息循环的线程上创建接收窗口。
    /// </summary>
    public static void Initialize()
    {
        lock (Gate)
        {
            if (_initialized)
            {
                return;
            }

            _initialized = true;
            _isDark = ReadSystemTheme();
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        }
    }

    /// <summary>
    /// 重新读取系统主题并同步（窗口 Shown/显示时兜底调用，应对 SystemEvents 事件未触发的极端场景）。
    /// </summary>
    public static void Refresh()
    {
        SetDark(ReadSystemTheme());
    }

    /// <summary>
    /// 强制应用指定主题（令牌层映射单测与手动预览入口）。
    /// 系统主题事件仍优先：用户切换系统主题时 <see cref="Refresh"/> 会把它纠正回系统值。
    /// </summary>
    public static void ApplyTheme(bool dark)
    {
        SetDark(dark);
    }

    private static void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category == UserPreferenceCategory.General)
        {
            Refresh();
        }
    }

    private static void SetDark(bool dark)
    {
        EventHandler? handler;
        lock (Gate)
        {
            if (_isDark == dark)
            {
                return;
            }

            _isDark = dark;
            handler = ThemeChanged;
        }

        // 事件在锁外触发，避免事件处理器回调 Refresh() 时重入死锁（CLAUDE.md 7.4）
        handler?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>读取 Windows 应用主题：AppsUseLightTheme=0 即深色（Win10 1607+/Win11 均有效）。</summary>
    private static bool ReadSystemTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int v)
            {
                return v == 0;
            }
        }
        catch
        {
            // 注册表不可读时保持浅色默认，不抛异常影响启动
        }

        return false;
    }
}
