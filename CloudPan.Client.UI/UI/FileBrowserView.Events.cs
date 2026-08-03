using CloudPan.Client.Core.Services;
using CloudPan.Infrastructure.Design;

namespace CloudPan.Client.UI;

/// <summary>FileBrowserView 部分类：工具栏/操作按钮/列表列点击与拖放事件处理器（具名方法，CP301）。</summary>
public partial class FileBrowserView
{
    // ================================================================
    // 事件处理（具名方法，CP301）
    // ================================================================

    private void UpButton_Click(object? sender, EventArgs e) => UpRequested?.Invoke();

    private void SearchBox_TextChanged(object? sender, EventArgs e) => SearchTextChanged?.Invoke(_searchBox.Text);

    private void ViewListButton_Click(object? sender, EventArgs e)
    {
        if (_list.View != View.Details)
        {
            _list.View = View.Details;
            UpdateViewToggle();
            RenderList();
        }
    }

    private void ViewGridButton_Click(object? sender, EventArgs e)
    {
        if (_list.View != View.LargeIcon)
        {
            _list.View = View.LargeIcon;
            UpdateViewToggle();
            RenderList();
        }
    }

    private void SortCombo_SelectedIndexChanged(object? sender, EventArgs e)
    {
        if (_syncingSortCombo)
        {
            return;
        }

        _sortMode = _sortCombo.SelectedIndex switch
        {
            1 => "大小",
            2 => "类型",
            _ => "名称"
        };
        _sortAscending = true; // 下拉选择重置为升序
        RenderList();
    }

    private void List_ColumnClick(object? sender, ColumnClickEventArgs e)
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

        if (_sortMode == mode)
        {
            _sortAscending = !_sortAscending;
        }
        else
        {
            _sortMode = mode;
            _sortAscending = true;
        }

        // 同步排序下拉选中项（抑制其事件，避免二次重绘）
        _syncingSortCombo = true;
        _sortCombo.SelectedIndex = mode switch
        {
            "大小" => 1,
            "类型" => 2,
            _ => 0
        };
        _syncingSortCombo = false;
        RenderList();
    }

    private void List_SelectedIndexChanged(object? sender, EventArgs e)
    {
        UpdateSelection(); // T-083：从 ListView 选中项整体同步（多选/单选/空）
    }

    /// <summary>T-014/T-083：点击「删除」/「批量删除」→ 转发全部选中项给宿主（逐项进回收站）。</summary>
    private void DeleteButton_Click(object? sender, EventArgs e)
    {
        List<FileBrowseItem> items = GetSelectedItems();
        if (items.Count > 0)
        {
            DeleteRequested?.Invoke(items);
        }
    }

    /// <summary>T-014：点击「回收站」→ 打开最近删除入口。</summary>
    private void TrashButton_Click(object? sender, EventArgs e) => TrashRequested?.Invoke();

    /// <summary>T-018：点击「分享」→ 转发选中的文件给宿主（创建/撤销分享）。</summary>
    private void ShareButton_Click(object? sender, EventArgs e)
    {
        if (SelectedItem != null && !SelectedItem.IsDirectory)
        {
            ShareRequested?.Invoke(SelectedItem);
        }
    }

    /// <summary>T-018：点击「版本」→ 转发选中的文件给宿主（版本历史列表/回滚）。</summary>
    private void VersionButton_Click(object? sender, EventArgs e)
    {
        if (SelectedItem != null && !SelectedItem.IsDirectory)
        {
            VersionHistoryRequested?.Invoke(SelectedItem);
        }
    }

    private void List_ItemActivate(object? sender, EventArgs e)
    {
        if (_list.SelectedItems.Count == 0 || _list.SelectedItems[0].Tag is not FileBrowseItem item)
        {
            return;
        }

        if (item.IsDirectory)
        {
            DirectoryActivated?.Invoke(item.Path);
        }
        else
        {
            FileActivated?.Invoke(item);
        }
    }

    /// <summary>T-033：点击「上传」→ 多选文件对话框 → 转发文件路径给宿主（复制入同步根并入队上传）。</summary>
    private void UploadButton_Click(object? sender, EventArgs e)
    {
        using OpenFileDialog ofd = new OpenFileDialog
        {
            Multiselect = true,
            Title = "选择要上传的文件",
            CheckFileExists = true,
        };
        if (ofd.ShowDialog(this) == DialogResult.OK && ofd.FileNames.Length > 0)
        {
            UploadRequested?.Invoke(ofd.FileNames);
        }
    }

    /// <summary>T-033/T-083：点击「下载到本机」→ 转发可下载的选中 CloudOnly 文件列表给宿主（按需下载）。</summary>
    private void DownloadButton_Click(object? sender, EventArgs e)
    {
        List<FileBrowseItem> items = GetDownloadableSelection();
        if (items.Count > 0)
        {
            DownloadRequested?.Invoke(items);
        }
    }

    /// <summary>T-033：拖拽进入浏览视图：仅接受文件拖放（显示复制效果）。</summary>
    private void FileBrowser_DragEnter(object? sender, DragEventArgs e)
    {
        e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true
            ? DragDropEffects.Copy
            : DragDropEffects.None;
    }

    /// <summary>T-033：拖放文件到浏览视图：收集文件路径交宿主导入同步根。</summary>
    private void FileBrowser_DragDrop(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
        {
            FilesDropped?.Invoke(files);
        }
    }
}
