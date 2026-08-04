using CloudPan.Client.Core.Services;
using CloudPan.Infrastructure.Design;

namespace CloudPan.Client.UI;

/// <summary>文件浏览列表渲染协作类（T-109）：排序构造交 FileBrowseRender，负责重绘/保留选中/视图切换高亮。</summary>
internal sealed class FileBrowserListRenderer
{
    private readonly FileBrowserView _view;

    public FileBrowserListRenderer(FileBrowserView view)
    {
        _view = view;
    }

    /// <summary>按当前排序/视图模式渲染列表，保留选中项（列表项构造在 FileBrowseRender；网格缩略图交 ThumbnailLoader）。</summary>
    public void RenderList()
    {
        bool grid = _view._list.View == View.LargeIcon;
        List<ListViewItem> items = FileBrowseRender.BuildItems(
            _view._currentItems, _view._sortMode, _view._sortAscending, grid, _view.ResolveState);

        _view._list.BeginUpdate();
        try
        {
            _view._list.Items.Clear();
            _view._list.Items.AddRange(items.ToArray());
            _view._list.Visible = items.Count > 0;
            _view._emptyLabel.Visible = items.Count == 0;
            _view._emptyLabel.Text = _view._isSearchActive ? "未找到匹配的文件" : "此文件夹为空\n将文件放入同步目录即可自动同步";

            RestoreSelection();
        }
        finally
        {
            _view._list.EndUpdate();
        }

        _view._thumbs.RenderGrid(items, grid); // T-087：网格视图为图片项异步加载缩略图（失败回退字形），列表视图不动
    }

    /// <summary>T-083：保留上次选中的全部项（周期刷新时不丢失多选）。</summary>
    private void RestoreSelection()
    {
        bool first = true;
        foreach (ListViewItem lvi in _view._list.Items)
        {
            if (lvi.Tag is FileBrowseItem item &&
                _view._selectedPaths.Contains(item.Path, StringComparer.OrdinalIgnoreCase))
            {
                lvi.Selected = true;
                if (first)
                {
                    lvi.EnsureVisible();
                    first = false;
                }
            }
        }

        _view.UpdateSelection();
    }

    /// <summary>更新列表/网格切换按钮的高亮状态。</summary>
    public void UpdateViewToggle()
    {
        bool grid = _view._list.View == View.LargeIcon;
        _view._listViewButton.BackColor = grid ? CloudPanColors.BackgroundLight : CloudPanColors.AccentBlue;
        _view._listViewButton.ForeColor = grid ? CloudPanColors.TextPrimary : CloudPanColors.TextOnPrimary;
        _view._gridViewButton.BackColor = grid ? CloudPanColors.AccentBlue : CloudPanColors.BackgroundLight;
        _view._gridViewButton.ForeColor = grid ? CloudPanColors.TextOnPrimary : CloudPanColors.TextPrimary;
    }
}
