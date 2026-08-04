using CloudPan.Client.Core.Services;

namespace CloudPan.Client.UI;

/// <summary>文件浏览视图事件分派协作类（T-109）：工具栏/操作按钮/列表列点击与拖放事件处理器（具名方法，CP301）。</summary>
internal sealed class FileBrowserEvents
{
    private readonly FileBrowserView _view;
    private readonly FileBrowserListRenderer _renderer;

    public FileBrowserEvents(FileBrowserView view, FileBrowserListRenderer renderer)
    {
        _view = view;
        _renderer = renderer;
    }

    internal void UpButton_Click(object? sender, EventArgs e) => _view.RaiseUpRequested();

    internal void SearchBox_TextChanged(object? sender, EventArgs e) => _view.RaiseSearchTextChanged(_view._searchBox.Text);

    internal void ViewListButton_Click(object? sender, EventArgs e)
    {
        if (_view._list.View != View.Details)
        {
            _view._list.View = View.Details;
            _renderer.UpdateViewToggle();
            _renderer.RenderList();
        }
    }

    internal void ViewGridButton_Click(object? sender, EventArgs e)
    {
        if (_view._list.View != View.LargeIcon)
        {
            _view._list.View = View.LargeIcon;
            _renderer.UpdateViewToggle();
            _renderer.RenderList();
        }
    }

    internal void SortCombo_SelectedIndexChanged(object? sender, EventArgs e)
    {
        if (_view._syncingSortCombo)
        {
            return;
        }

        _view._sortMode = _view._sortCombo.SelectedIndex switch
        {
            1 => "大小",
            2 => "类型",
            _ => "名称"
        };
        _view._sortAscending = true; // 下拉选择重置为升序
        _renderer.RenderList();
    }

    internal void List_ColumnClick(object? sender, ColumnClickEventArgs e)
    {
        string? mode = e.Column switch
        {
            1 => "名称",
            2 => "大小",
            3 => "类型",
            _ => null
        };
        if (mode == null)
        {
            return;
        }

        if (_view._sortMode == mode)
        {
            _view._sortAscending = !_view._sortAscending;
        }
        else
        {
            _view._sortMode = mode;
            _view._sortAscending = true;
        }

        // 同步排序下拉选中项（抑制其事件，避免二次重绘）
        _view._syncingSortCombo = true;
        _view._sortCombo.SelectedIndex = mode switch
        {
            "大小" => 1,
            "类型" => 2,
            _ => 0
        };
        _view._syncingSortCombo = false;
        _renderer.RenderList();
    }

    internal void List_SelectedIndexChanged(object? sender, EventArgs e)
    {
        _view.UpdateSelection(); // T-083：从 ListView 选中项整体同步（多选/单选/空）
    }

    /// <summary>T-014/T-083：点击「删除」/「批量删除」→ 转发全部选中项给宿主（逐项进回收站）。</summary>
    internal void DeleteButton_Click(object? sender, EventArgs e)
    {
        List<FileBrowseItem> items = _view.GetSelectedItems();
        if (items.Count > 0)
        {
            _view.RaiseDeleteRequested(items);
        }
    }

    /// <summary>T-014：点击「回收站」→ 打开最近删除入口。</summary>
    internal void TrashButton_Click(object? sender, EventArgs e) => _view.RaiseTrashRequested();

    /// <summary>T-018：点击「分享」→ 转发选中的文件给宿主（创建/撤销分享）。</summary>
    internal void ShareButton_Click(object? sender, EventArgs e)
    {
        if (_view.SelectedItem != null && !_view.SelectedItem.IsDirectory)
        {
            _view.RaiseShareRequested(_view.SelectedItem);
        }
    }

    /// <summary>T-018：点击「版本」→ 转发选中的文件给宿主（版本历史列表/回滚）。</summary>
    internal void VersionButton_Click(object? sender, EventArgs e)
    {
        if (_view.SelectedItem != null && !_view.SelectedItem.IsDirectory)
        {
            _view.RaiseVersionHistoryRequested(_view.SelectedItem);
        }
    }

    internal void List_ItemActivate(object? sender, EventArgs e)
    {
        if (_view._list.SelectedItems.Count == 0 || _view._list.SelectedItems[0].Tag is not FileBrowseItem item)
        {
            return;
        }

        if (item.IsDirectory)
        {
            _view.RaiseDirectoryActivated(item.Path);
        }
        else
        {
            _view.RaiseFileActivated(item);
        }
    }

    /// <summary>T-033：点击「上传」→ 多选文件对话框 → 转发文件路径给宿主（复制入同步根并入队上传）。</summary>
    internal void UploadButton_Click(object? sender, EventArgs e)
    {
        using OpenFileDialog ofd = new OpenFileDialog
        {
            Multiselect = true,
            Title = "选择要上传的文件",
            CheckFileExists = true,
        };
        if (ofd.ShowDialog(_view) == DialogResult.OK && ofd.FileNames.Length > 0)
        {
            _view.RaiseUploadRequested(ofd.FileNames);
        }
    }

    /// <summary>T-033/T-083：点击「下载到本机」→ 转发可下载的选中 CloudOnly 文件列表给宿主（按需下载）。</summary>
    internal void DownloadButton_Click(object? sender, EventArgs e)
    {
        List<FileBrowseItem> items = _view.GetDownloadableSelection();
        if (items.Count > 0)
        {
            _view.RaiseDownloadRequested(items);
        }
    }

    /// <summary>T-033：拖拽进入浏览视图：仅接受文件拖放（显示复制效果）。</summary>
    internal void FileBrowser_DragEnter(object? sender, DragEventArgs e)
    {
        e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true
            ? DragDropEffects.Copy
            : DragDropEffects.None;
    }

    /// <summary>T-033：拖放文件到浏览视图：收集文件路径交宿主导入同步根。</summary>
    internal void FileBrowser_DragDrop(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
        {
            _view.RaiseFilesDropped(files);
        }
    }
}
