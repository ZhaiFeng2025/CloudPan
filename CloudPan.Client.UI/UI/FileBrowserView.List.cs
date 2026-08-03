using CloudPan.Client.Core.Services;
using CloudPan.Contract;
using CloudPan.Infrastructure.Design;

namespace CloudPan.Client.UI;

/// <summary>FileBrowserView 部分类：列表渲染（排序/构造交 FileBrowseRender）、多选选中管理与视图切换。</summary>
public partial class FileBrowserView
{
    // ================================================================
    // 渲染
    // ================================================================

    /// <summary>按当前排序/视图模式渲染列表，保留选中项（列表项构造在 FileBrowseRender；网格缩略图交 ThumbnailLoader）。</summary>
    private void RenderList()
    {
        bool grid = _list.View == View.LargeIcon;
        List<ListViewItem> items = FileBrowseRender.BuildItems(
            _currentItems, _sortMode, _sortAscending, grid, ResolveState);

        _list.BeginUpdate();
        try
        {
            _list.Items.Clear();
            _list.Items.AddRange(items.ToArray());
            _list.Visible = items.Count > 0;
            _emptyLabel.Visible = items.Count == 0;
            _emptyLabel.Text = _isSearchActive ? "未找到匹配的文件" : "此文件夹为空\n将文件放入同步目录即可自动同步";

            RestoreSelection();
        }
        finally
        {
            _list.EndUpdate();
        }

        _thumbs.RenderGrid(items, grid); // T-087：网格视图为图片项异步加载缩略图（失败回退字形），列表视图不动
    }

    /// <summary>T-083：保留上次选中的全部项（周期刷新时不丢失多选）。</summary>
    private void RestoreSelection()
    {
        bool first = true;
        foreach (ListViewItem lvi in _list.Items)
        {
            if (lvi.Tag is FileBrowseItem item &&
                _selectedPaths.Contains(item.Path, StringComparer.OrdinalIgnoreCase))
            {
                lvi.Selected = true;
                if (first)
                {
                    lvi.EnsureVisible();
                    first = false;
                }
            }
        }

        UpdateSelection();
    }

    /// <summary>T-083：从 ListView 选中项同步状态——SelectedItem（首个）/批量删除按钮文本与可用性/分享版本仅单选文件/下载仅 CloudOnly 子集。</summary>
    private void UpdateSelection()
    {
        List<FileBrowseItem> selected = GetSelectedItems();

        SelectedItem = selected.Count > 0 ? selected[0] : null;
        _selectedPaths = selected.Select(i => i.Path).ToList();

        _deleteButton.Enabled = selected.Count > 0;
        _deleteButton.Text = selected.Count > 1 ? "批量删除" : "删除";

        bool singleFile = selected.Count == 1 && !selected[0].IsDirectory;
        _shareButton.Enabled = singleFile;
        _versionButton.Enabled = singleFile;

        _downloadButton.Enabled = GetDownloadableSelection().Count > 0;
    }

    /// <summary>将 FileBrowseItem 映射为（图标, 颜色）双通道；未注入 StateResolver 时用 FileBrowseRender 默认映射。</summary>
    private (string Icon, Color Color) ResolveState(FileBrowseItem item)
    {
        return StateResolver != null ? StateResolver(item) : FileBrowseRender.ResolveDefaultState(item);
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
}
