using System.Diagnostics;
using System.Drawing.Drawing2D;
using CloudPan.Client.Core.Models;
using CloudPan.Client.Core.Services;
using CloudPan.Contract;
using CloudPan.Infrastructure.Design;

namespace CloudPan.Client.UI;

/// <summary>MainWindow 部分类：统一日志列表与过滤。</summary>
public partial class MainWindow
{

    // ================================================================
    // 日志过滤（统一列表 + 过滤下拉框）
    // ================================================================

    /// <summary>线程安全地向日志添加消息。</summary>
    public void AddLog(string message)
    {
        if (InvokeRequired)
        {
            Invoke(() => AddLogCore(message));
            return;
        }
        AddLogCore(message);
    }

    private void AddLogCore(string message)
    {
        string formatted = FormatLogMessage(message);
        _allLogEntries.Add(formatted);

        // 根据当前过滤模式决定是否显示
        int filter = _logFilterComboBox.SelectedIndex;
        bool shouldShow = filter switch
        {
            0 => true,
            1 => IsFileOperationEntry(formatted),
            2 => IsErrorEntry(formatted),
            _ => true
        };

        if (shouldShow)
        {
            _logList.Items.Add(formatted);
            while (_logList.Items.Count > MaxLogItems + 1) // +1 保留表头
            {
                _logList.Items.RemoveAt(1); // 保留第一条表头
            }

            if (_logList.Items.Count > 0)
            {
                _logList.TopIndex = _logList.Items.Count - 1;
            }
        }
    }

    /// <summary>格式化日志消息——添加图标前缀、时间戳、路径简化和截断。</summary>
    private static string FormatLogMessage(string message)
    {
        string icon = message switch
        {
            string s when s.Contains("上传完成") || s.Contains("下载完成") => "✅ ",
            string s when s.Contains("失败") || s.Contains("异常") => "❌ ",
            string s when s.Contains("冲突") => "⚠️ ",
            string s when s.Contains("上传") || s.Contains("下载") || s.Contains("同步") => "🔄 ",
            string s when s.Contains("删除") => "🗑️ ",
            string s when s.Contains("重命名") => "✏️ ",
            _ => "📋 "
        };

        // 提取路径简化为文件名
        string display = message;
        if (message.Contains('/'))
        {
            string[] parts = message.Split(' ');
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i].StartsWith("/"))
                {
                    parts[i] = Path.GetFileName(parts[i]);
                    display = string.Join(" ", parts).Replace("  ", " ");
                }
            }
        }
        if (display.Length > 80)
        {
            display = display[..77] + "...";
        }

        return $"{icon}[{DateTime.Now:HH:mm:ss}] {display}";
    }

    /// <summary>判断日志条目是否为文件操作类（根据图标前缀）。</summary>
    private static bool IsFileOperationEntry(string entry)
    {
        return entry.Contains("✅ ") || entry.Contains("❌ ") || entry.Contains("🔄 ") ||
               entry.Contains("🗑️ ") || entry.Contains("✏️ ");
    }

    /// <summary>判断日志条目是否为错误类。</summary>
    private static bool IsErrorEntry(string entry)
    {
        return entry.Contains("❌ ") || entry.Contains("失败") || entry.Contains("错误") || entry.Contains("异常");
    }

    /// <summary>过滤下拉框变更时重新填充日志列表。</summary>
    private void ApplyLogFilter()
    {
        _logList.Items.Clear();

        // 重新添加表头
        var ver = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version;
        string verStr = ver != null ? $"v{ver.Major}.{ver.Minor}.{ver.Build}" : "(开发版本)";
        _logList.Items.Add($"CloudPan 客户端 {verStr}");

        int filter = _logFilterComboBox.SelectedIndex;

        // 从最新的条目开始反向填充，保留最后 MaxLogItems 条匹配的条目
        int count = 0;
        for (int i = _allLogEntries.Count - 1; i >= 0 && count < MaxLogItems; i--)
        {
            string entry = _allLogEntries[i];
            bool show = filter switch
            {
                0 => true,
                1 => IsFileOperationEntry(entry),
                2 => IsErrorEntry(entry),
                _ => true
            };

            if (show)
            {
                _logList.Items.Insert(1, entry); // 插入到表头之后
                count++;
            }
        }

        if (_logList.Items.Count > 0)
        {
            _logList.TopIndex = _logList.Items.Count - 1;
        }
    }
}
