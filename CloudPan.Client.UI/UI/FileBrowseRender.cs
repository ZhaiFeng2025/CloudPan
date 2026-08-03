using CloudPan.Client.Core.Services;
using CloudPan.Contract;
using CloudPan.Infrastructure.Design;

namespace CloudPan.Client.UI;

/// <summary>
/// 文件浏览渲染纯助手（T-083 行数门禁抽取，聚合行数控制）——排序/列表项构造/格式化/状态映射/字形绘制。
/// 无 UI 状态、无控件持有，FileBrowserView 经静态调用。
/// </summary>
internal static class FileBrowseRender
{
    /// <summary>按排序模式排序：目录优先，同组内按名称/大小/类型（升序或降序）。</summary>
    public static List<FileBrowseItem> Sort(IReadOnlyList<FileBrowseItem> items, string sortMode, bool sortAscending)
    {
        List<FileBrowseItem> list = items.ToList();
        list.Sort((a, b) =>
        {
            int byDir = (b.IsDirectory ? 1 : 0).CompareTo(a.IsDirectory ? 1 : 0);
            if (byDir != 0)
            {
                return byDir;
            }

            int cmp = sortMode switch
            {
                "大小" => a.Size.CompareTo(b.Size),
                "类型" => string.Compare(GetTypeLabel(a), GetTypeLabel(b), StringComparison.OrdinalIgnoreCase),
                _ => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase)
            };
            return sortAscending ? cmp : -cmp;
        });
        return list;
    }

    /// <summary>按排序/视图模式构造列表项（不含控件交互），state 由调用方注入（含本地错误/冲突覆盖）。</summary>
    public static List<ListViewItem> BuildItems(
        IReadOnlyList<FileBrowseItem> items, string sortMode, bool sortAscending, bool grid,
        Func<FileBrowseItem, (string Icon, Color Color)> resolve)
    {
        var result = new List<ListViewItem>(items.Count);
        foreach (FileBrowseItem item in Sort(items, sortMode, sortAscending))
        {
            (string icon, Color color) = resolve(item);
            ListViewItem lvi;
            if (grid)
            {
                lvi = new ListViewItem($"{icon} {item.Name}")
                {
                    ImageIndex = item.IsDirectory ? 0 : 1,
                    ForeColor = color,
                    Tag = item,
                };
            }
            else
            {
                lvi = new ListViewItem(icon) { ForeColor = color, Tag = item };
                lvi.SubItems.Add(item.Name);
                lvi.SubItems.Add(item.IsDirectory ? "" : FormatSize(item.Size));
                lvi.SubItems.Add(GetTypeLabel(item));
            }

            result.Add(lvi);
        }
        return result;
    }

    /// <summary>格式化文件大小为人类可读形式（B/KB/MB/GB）。</summary>
    public static string FormatSize(long bytes)
    {
        return bytes switch
        {
            < 1024 => $"{bytes} B",
            < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
            < 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024.0):F1} MB",
            _ => $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB"
        };
    }

    /// <summary>获取类型标签：目录→"文件夹"，文件→扩展名（含点），无扩展名→"文件"。</summary>
    public static string GetTypeLabel(FileBrowseItem item)
    {
        if (item.IsDirectory)
        {
            return "文件夹";
        }

        int idx = item.Name.LastIndexOf('.');
        return idx >= 0 && idx < item.Name.Length - 1 ? item.Name[idx..] : "文件";
    }

    /// <summary>默认 FileState →（图标, 颜色）双通道映射（未注入 StateResolver 时使用）。</summary>
    public static (string Icon, Color Color) ResolveDefaultState(FileBrowseItem item)
    {
        return item.State switch
        {
            (int)FileState.Synced => ("✓", CloudPanColors.SuccessGreen),
            (int)FileState.Uploading => ("↻", CloudPanColors.AccentBlue),
            (int)FileState.Downloading => ("↻", CloudPanColors.AccentBlue),
            (int)FileState.Modified => ("↻", CloudPanColors.AccentBlue),
            (int)FileState.CloudOnly => ("☁", CloudPanColors.TextMuted),
            (int)FileState.Conflict => ("!", CloudPanColors.WarningOrange),
            _ => ("✓", CloudPanColors.SuccessGreen)
        };
    }

    /// <summary>绘制文件夹字形（40×40）：黄色圆角矩形 + 顶部标签。</summary>
    public static Image DrawFolderGlyph()
    {
        Bitmap bmp = new Bitmap(40, 40);
        using Graphics g = Graphics.FromImage(bmp);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);
        using (SolidBrush tab = new SolidBrush(Color.FromArgb(255, 205, 90)))
        {
            g.FillRectangle(tab, 5, 7, 11, 7);
        }

        using (SolidBrush body = new SolidBrush(Color.FromArgb(255, 214, 102)))
        {
            g.FillRectangle(body, 4, 11, 32, 23);
        }

        using Pen outline = new Pen(Color.FromArgb(210, 160, 50), 1.5f);
        g.DrawRectangle(outline, 4, 11, 32, 23);
        return bmp;
    }

    /// <summary>绘制文件字形（40×40）：白色页面 + 折叠角。</summary>
    public static Image DrawFileGlyph()
    {
        Bitmap bmp = new Bitmap(40, 40);
        using Graphics g = Graphics.FromImage(bmp);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);
        using (SolidBrush body = new SolidBrush(Color.FromArgb(250, 250, 250)))
        {
            g.FillRectangle(body, 6, 6, 28, 28);
        }

        Point[] fold = { new(20, 6), new(34, 6), new(34, 20), new(20, 20) };
        using (SolidBrush foldBrush = new SolidBrush(Color.FromArgb(190, 195, 200)))
        {
            g.FillPolygon(foldBrush, fold);
        }

        using Pen outline = new Pen(Color.FromArgb(170, 175, 180), 1.5f);
        g.DrawRectangle(outline, 6, 6, 28, 28);
        g.DrawLine(outline, 20, 6, 20, 20);
        g.DrawLine(outline, 20, 20, 34, 20);
        return bmp;
    }
}
