namespace CloudPan.Client.UI;

/// <summary>文件浏览右键菜单协作类（T-109）：构建菜单（下载/分享/删除/版本历史/打开）与菜单项点击处理。</summary>
internal sealed class FileBrowserContextMenu
{
    private readonly FileBrowserView _view;

    public FileBrowserContextMenu(FileBrowserView view)
    {
        _view = view;
    }

    /// <summary>T-083：构建文件列表右键菜单（重命名/移动/复制列为 v1.1）；可用性刷新由视图 ListMenu_Opening 承担。</summary>
    public void Build()
    {
        _view._listMenu = new ContextMenuStrip();
        _view._menuOpenItem = new ToolStripMenuItem("打开");
        _view._menuDownloadItem = new ToolStripMenuItem("下载到本机");
        _view._menuShareItem = new ToolStripMenuItem("分享");
        _view._menuVersionItem = new ToolStripMenuItem("版本历史");
        _view._menuDeleteItem = new ToolStripMenuItem("删除");
        _view._menuOpenItem.Click += ListMenu_Open_Click;
        _view._menuDownloadItem.Click += ListMenu_Download_Click;
        _view._menuShareItem.Click += ListMenu_Share_Click;
        _view._menuVersionItem.Click += ListMenu_Version_Click;
        _view._menuDeleteItem.Click += ListMenu_Delete_Click;
        _view._listMenu.Items.AddRange(new ToolStripItem[]
        {
            _view._menuOpenItem, _view._menuDownloadItem, _view._menuShareItem, _view._menuVersionItem,
            new ToolStripSeparator(), _view._menuDeleteItem,
        });
        _view._listMenu.Opening += _view.ListMenu_Opening;
        _view._list.ContextMenuStrip = _view._listMenu;
        _view._list.MouseDown += List_MouseDown;
    }

    /// <summary>T-083：右键落在未选中项上时先切换选中，避免菜单作用于上一次选中的项。</summary>
    private void List_MouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Right) return;
        ListViewItem? hit = _view._list.HitTest(e.X, e.Y).Item;
        if (hit != null && !hit.Selected)
        {
            _view._list.SelectedItems.Clear();
            hit.Selected = true;
        }
    }

    private void ListMenu_Open_Click(object? sender, EventArgs e)
    {
        if (_view.SelectedItem is { IsDirectory: false } item) _view.RaiseFileActivated(item);
    }

    private void ListMenu_Download_Click(object? sender, EventArgs e)
    {
        var items = _view.GetDownloadableSelection();
        if (items.Count > 0) _view.RaiseDownloadRequested(items);
    }

    private void ListMenu_Share_Click(object? sender, EventArgs e)
    {
        if (_view.SelectedItem is { IsDirectory: false } item) _view.RaiseShareRequested(item);
    }

    private void ListMenu_Version_Click(object? sender, EventArgs e)
    {
        if (_view.SelectedItem is { IsDirectory: false } item) _view.RaiseVersionHistoryRequested(item);
    }

    private void ListMenu_Delete_Click(object? sender, EventArgs e)
    {
        var items = _view.GetSelectedItems();
        if (items.Count > 0) _view.RaiseDeleteRequested(items);
    }
}
