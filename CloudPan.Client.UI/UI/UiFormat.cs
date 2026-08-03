using System.Drawing;
using System.Windows.Forms;

namespace CloudPan.Client.UI;

/// <summary>
/// 共享格式化工具（T-070 拆分）：文件大小与传输速率的人类可读形式。
/// 由 MainWindow 各部分（状态/回收站/版本/冲突）与 ConflictResolutionDialog 共用。
/// </summary>
internal static class UiFormat
{
    /// <summary>格式化文件大小为人类可读形式（B/KB/MB/GB）。</summary>
    public static string FormatFileSize(long bytes)
    {
        return bytes switch
        {
            < 1024 => $"{bytes} B",
            < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
            < 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024.0):F1} MB",
            _ => $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB"
        };
    }

    /// <summary>格式化数据传输速率（字节/秒 → "12.3 MB" 形式，小于 1MB 时显示 KB）。</summary>
    public static string FormatDataRate(double bytesPerSecond)
    {
        return bytesPerSecond switch
        {
            < 1024 => $"{bytesPerSecond:F0} B",
            < 1024 * 1024 => $"{bytesPerSecond / 1024.0:F1} KB",
            < 1024 * 1024 * 1024 => $"{bytesPerSecond / (1024.0 * 1024.0):F1} MB",
            _ => $"{bytesPerSecond / (1024.0 * 1024.0 * 1024.0):F2} GB"
        };
    }
}
