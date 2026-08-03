using System.ComponentModel;
using CloudPan.Client.Core.Services;
using CloudPan.Contract;

namespace CloudPan.Client.UI;

/// <summary>FileBrowserView 部分类：右键上下文菜单（T-083，下载/分享/删除/版本历史/打开）与多选选中集合工具。</summary>
public partial class FileBrowserView
{
    private ContextMenuStrip _listMenu = null!;
    private ToolStripMenuItem _menuOpenItem = null!;
    private ToolStripMenuItem _menuDownloadItem = null!;
    private ToolStripMenuItem _menuShareItem = null!;
    private ToolStripMenuItem _menuVersionItem = null!;
    private ToolStripMenuItem _menuDeleteItem = null!;

    /// <summary>T-083：构建文件列表右键菜单（重命名/移动/复制列为 v1.1）。</summary>
    private void BuildListMenu()
    {
        _listMenu = new ContextMenuStrip();
        _menuOpenItem = new ToolStripMenuItem("打开");
        _menuDownloadItem = new ToolStripMenuItem("下载到本机");
        _menuShareItem = new ToolStripMenuItem("分享");
        _menuVersionItem = new ToolStripMenuItem("版本历史");
        _menuDeleteItem = new ToolStripMenuItem("删除");
        _menuOpenItem.Click += ListMenu_Open_Click;
        _menuDownloadItem.Click += ListMenu_Download_Click;
        _menuShareItem.Click += ListMenu_Share_Click;
        _menuVersionItem.Click += ListMenu_Version_Click;
        _menuDeleteItem.Click += ListMenu_Delete_Click;
        _listMenu.Items.AddRange(new ToolStripItem[]
        {
            _menuOpenItem, _menuDownloadItem, _menuShareItem, _menuVersionItem,
            new ToolStripSeparator(), _menuDeleteItem,
        });
        _listMenu.Opening += ListMenu_Opening;
        _list.ContextMenuStrip = _listMenu;
        _list.MouseDown += List_MouseDown;
    }

    /// <summary>T-083：右键落在未选中项上时先切换选中，避免菜单作用于上一次选中的项。</summary>
    private void List_MouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Right) return;
        ListViewItem? hit = _list.HitTest(e.X, e.Y).Item;
        if (hit != null && !hit.Selected)
        {
            _list.SelectedItems.Clear();
            hit.Selected = true;
        }
    }

    /// <summary>T-083：右键菜单弹出前按当前选中刷新可用性——分享/版本/打开仅单选文件，下载仅 CloudOnly 子集，删除任意选中。</summary>
    private void ListMenu_Opening(object? sender, CancelEventArgs e)
    {
        List<FileBrowseItem> selected = GetSelectedItems();
        bool singleFile = selected.Count == 1 && !selected[0].IsDirectory;
        _menuOpenItem.Enabled = singleFile;
        _menuDownloadItem.Enabled = GetDownloadableSelection().Count > 0;
        _menuShareItem.Enabled = singleFile;
        _menuVersionItem.Enabled = singleFile;
        _menuDeleteItem.Enabled = selected.Count > 0;
    }

    private void ListMenu_Open_Click(object? sender, EventArgs e)
    {
        if (SelectedItem is { IsDirectory: false } item) FileActivated?.Invoke(item);
    }

    private void ListMenu_Download_Click(object? sender, EventArgs e)
    {
        var items = GetDownloadableSelection();
        if (items.Count > 0) DownloadRequested?.Invoke(items);
    }

    private void ListMenu_Share_Click(object? sender, EventArgs e)
    {
        if (SelectedItem is { IsDirectory: false } item) ShareRequested?.Invoke(item);
    }

    private void ListMenu_Version_Click(object? sender, EventArgs e)
    {
        if (SelectedItem is { IsDirectory: false } item) VersionHistoryRequested?.Invoke(item);
    }

    private void ListMenu_Delete_Click(object? sender, EventArgs e)
    {
        var items = GetSelectedItems();
        if (items.Count > 0) DeleteRequested?.Invoke(items);
    }

    // ================================================================
    // 多选选中集合工具（T-083）
    // ================================================================

    /// <summary>当前 ListView 全部选中项（按选中顺序），Tag 非 FileBrowseItem 时跳过。</summary>
    private List<FileBrowseItem> GetSelectedItems()
    {
        var list = new List<FileBrowseItem>();
        foreach (ListViewItem lvi in _list.SelectedItems)
        {
            if (lvi.Tag is FileBrowseItem item)
            {
                list.Add(item);
            }
        }
        return list;
    }

    /// <summary>可下载的选中子集：仅 CloudOnly 且本地不存在的文件（T-083 批量下载）。</summary>
    private List<FileBrowseItem> GetDownloadableSelection()
    {
        return GetSelectedItems()
            .Where(i => !i.IsDirectory && i.State == (int)FileState.CloudOnly && !i.LocalExists)
            .ToList();
    }
}
