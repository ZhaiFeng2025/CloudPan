using System.Drawing.Drawing2D;
using CloudPan.Client.Core.Services;
using CloudPan.Contract;
using CloudPan.Infrastructure.Design;

namespace CloudPan.Client.UI;

/// <summary>FileBrowserView 部分类：列表渲染、排序、选中管理、文件格式工具与列表项字形绘制。</summary>
public partial class FileBrowserView
{
    // ================================================================
    // 渲染
    // ================================================================

    /// <summary>按当前排序/视图模式渲染列表，保留选中项。</summary>
    private void RenderList()
    {
        List<FileBrowseItem> sorted = SortItems(_currentItems);
        bool grid = _list.View == View.LargeIcon;

        _list.BeginUpdate();
        try
        {
            _list.Items.Clear();
            foreach (FileBrowseItem item in sorted)
            {
                (string icon, Color color) = ResolveState(item);
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
                    lvi.SubItems.Add(item.IsDirectory ? "" : FormatFileSize(item.Size));
                    lvi.SubItems.Add(GetTypeLabel(item));
                }

                _list.Items.Add(lvi);
            }

            _list.Visible = sorted.Count > 0;
            _emptyLabel.Visible = sorted.Count == 0;
            _emptyLabel.Text = _isSearchActive ? "未找到匹配的文件" : "此文件夹为空\n将文件放入同步目录即可自动同步";

            RestoreSelection();
        }
        finally
        {
            _list.EndUpdate();
        }
    }

    /// <summary>按排序模式排序：目录优先，同组内按名称/大小/类型（升序或降序）。</summary>
    private List<FileBrowseItem> SortItems(IReadOnlyList<FileBrowseItem> items)
    {
        List<FileBrowseItem> list = items.ToList();
        list.Sort((a, b) =>
        {
            int byDir = (b.IsDirectory ? 1 : 0).CompareTo(a.IsDirectory ? 1 : 0);
            if (byDir != 0)
            {
                return byDir;
            }

            int cmp = _sortMode switch
            {
                "大小" => a.Size.CompareTo(b.Size),
                "类型" => string.Compare(GetTypeLabel(a), GetTypeLabel(b), StringComparison.OrdinalIgnoreCase),
                _ => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase)
            };
            return _sortAscending ? cmp : -cmp;
        });
        return list;
    }

    /// <summary>保留上次选中的项（周期刷新时不丢失选中）。</summary>
    private void RestoreSelection()
    {
        if (_selectedPath == null)
        {
            UpdateSelection(null);
            return;
        }

        foreach (ListViewItem lvi in _list.Items)
        {
            if (lvi.Tag is FileBrowseItem item && string.Equals(item.Path, _selectedPath, StringComparison.OrdinalIgnoreCase))
            {
                lvi.Selected = true;
                lvi.EnsureVisible();
                UpdateSelection(item);
                return;
            }
        }
    }

    /// <summary>同步选中项状态（SelectedItem / 删除按钮可用性；T-018 分享/版本仅对文件可用；T-033 下载仅对 CloudOnly 可用）。</summary>
    private void UpdateSelection(FileBrowseItem? item)
    {
        SelectedItem = item;
        _selectedPath = item?.Path;
        _deleteButton.Enabled = item != null;
        _shareButton.Enabled = item != null && !item.IsDirectory;
        _versionButton.Enabled = item != null && !item.IsDirectory;
        _downloadButton.Enabled = item != null && !item.IsDirectory
            && item.State == (int)FileState.CloudOnly && !item.LocalExists;
    }

    /// <summary>将 FileBrowseItem 映射为（图标, 颜色）双通道；未注入 StateResolver 时使用默认 FileState 映射。</summary>
    private (string Icon, Color Color) ResolveState(FileBrowseItem item)
    {
        if (StateResolver != null)
        {
            return StateResolver(item);
        }

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

    /// <summary>更新列表/网格切换按钮的高亮状态。</summary>
    private void UpdateViewToggle()
    {
        bool grid = _list.View == View.LargeIcon;
        _listViewButton.BackColor = grid ? CloudPanColors.BackgroundLight : CloudPanColors.AccentBlue;
        _listViewButton.ForeColor = grid ? CloudPanColors.TextPrimary : CloudPanColors.TextOnPrimary;
        _gridViewButton.BackColor = grid ? CloudPanColors.AccentBlue : CloudPanColors.BackgroundLight;
        _gridViewButton.ForeColor = grid ? CloudPanColors.TextOnPrimary : CloudPanColors.TextPrimary;
    }

    // ================================================================
    // 工具
    // ================================================================

    /// <summary>格式化文件大小为人类可读形式（B/KB/MB/GB）。</summary>
    private static string FormatFileSize(long bytes)
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
    private static string GetTypeLabel(FileBrowseItem item)
    {
        if (item.IsDirectory)
        {
            return "文件夹";
        }

        int idx = item.Name.LastIndexOf('.');
        return idx >= 0 && idx < item.Name.Length - 1 ? item.Name[idx..] : "文件";
    }

    /// <summary>绘制文件夹字形（40×40）：黄色圆角矩形 + 顶部标签。</summary>
    private static Image DrawFolderGlyph()
    {
        Bitmap bmp = new Bitmap(40, 40);
        using Graphics g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
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
    private static Image DrawFileGlyph()
    {
        Bitmap bmp = new Bitmap(40, 40);
        using Graphics g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
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
