using System.ComponentModel;
using CloudPan.Client.Core.Services;
using CloudPan.Contract;

namespace CloudPan.Client.UI;

/// <summary>
/// 文件浏览主视图（T-013）——纯渲染控件：面包屑导航 + 上一级 + 搜索 + 列表/网格切换 + 排序 + 每文件同步状态图标。
/// 数据由宿主（MainWindow）经 SyncEngine 加载后通过 <see cref="ShowItems"/> 注入，本控件只渲染与交互，不做数据访问。
/// 布局/事件分派/渲染/菜单/面包屑外提为协作类（T-109）：FileBrowserLayoutBuilder/FileBrowserEvents/
/// FileBrowserListRenderer/FileBrowserContextMenu/FileBrowserBreadcrumb。
/// </summary>
public partial class FileBrowserView : UserControl
{
    // ================================================================
    // 对外事件（由宿主处理导航/数据加载）
    // ================================================================

    /// <summary>目录激活（双击子目录 / 点击面包屑段）→ 参数为目录相对路径。</summary>
    public event Action<string>? DirectoryActivated;

    /// <summary>文件激活（双击文件）→ 参数为文件项（宿主据本地存在性/CloudOnly 决定打开或下载）。</summary>
    public event Action<FileBrowseItem>? FileActivated;

    /// <summary>点击「上传」（多选文件）→ 参数为选中的本地文件路径数组（T-033，宿主复制入同步根并入队上传）。</summary>
    public event Action<string[]>? UploadRequested;

    /// <summary>点击「下载到本机」（选中 CloudOnly 文件）→ 参数为可下载的选中文件列表（T-083 多选批量，宿主逐个 DownloadPathAsync）。</summary>
    public event Action<IReadOnlyList<FileBrowseItem>>? DownloadRequested;

    /// <summary>拖拽文件到浏览视图 → 参数为拖入的本地文件路径数组（T-033，宿主复制入同步根并入队上传）。</summary>
    public event Action<string[]>? FilesDropped;

    /// <summary>点击「上一级」。</summary>
    public event Action? UpRequested;

    /// <summary>搜索框内容变化 → 参数为当前搜索文本（可能为空串）。</summary>
    public event Action<string>? SearchTextChanged;

    /// <summary>点击「删除」/右键「删除」（有选中项）→ 参数为选中的文件/目录列表（T-083 多选批量，宿主逐项进回收站）。</summary>
    public event Action<IReadOnlyList<FileBrowseItem>>? DeleteRequested;

    /// <summary>点击「回收站」→ 打开最近删除入口（T-014）。</summary>
    public event Action? TrashRequested;

    /// <summary>点击「分享」（有选中文件）→ 参数为选中的文件（T-018，宿主负责创建/撤销分享）。</summary>
    public event Action<FileBrowseItem>? ShareRequested;

    /// <summary>点击「版本」（有选中文件）→ 参数为选中的文件（T-018，宿主负责版本历史列表/回滚）。</summary>
    public event Action<FileBrowseItem>? VersionHistoryRequested;

    // 事件触发（internal，供 T-109 外提协作类触发对外事件；C# 事件只能由声明类内部 Invoke）
    internal void RaiseDirectoryActivated(string path) => DirectoryActivated?.Invoke(path);
    internal void RaiseFileActivated(FileBrowseItem item) => FileActivated?.Invoke(item);
    internal void RaiseUploadRequested(string[] paths) => UploadRequested?.Invoke(paths);
    internal void RaiseDownloadRequested(IReadOnlyList<FileBrowseItem> items) => DownloadRequested?.Invoke(items);
    internal void RaiseFilesDropped(string[] paths) => FilesDropped?.Invoke(paths);
    internal void RaiseUpRequested() => UpRequested?.Invoke();
    internal void RaiseSearchTextChanged(string text) => SearchTextChanged?.Invoke(text);
    internal void RaiseDeleteRequested(IReadOnlyList<FileBrowseItem> items) => DeleteRequested?.Invoke(items);
    internal void RaiseTrashRequested() => TrashRequested?.Invoke();
    internal void RaiseShareRequested(FileBrowseItem item) => ShareRequested?.Invoke(item);
    internal void RaiseVersionHistoryRequested(FileBrowseItem item) => VersionHistoryRequested?.Invoke(item);

    /// <summary>状态解析器（由宿主注入，叠加本地错误/冲突覆盖）。未注入时使用默认 FileState → 图标/颜色映射。</summary>
    public Func<FileBrowseItem, (string Icon, Color Color)>? StateResolver { get; set; }

    /// <summary>缩略图获取器（宿主注入，T-087，指向 ApiClient.GetThumbnailAsync）：参数（path, width, ct）→ JPEG 字节，失败返回 null。</summary>
    public Func<string, int, CancellationToken, Task<byte[]?>>? ThumbnailFetcher
    {
        get => _thumbs.Fetcher;
        set => _thumbs.Fetcher = value;
    }

    /// <summary>当前浏览的目录相对路径（"/" 为根）。</summary>
    public string CurrentPath { get; private set; } = "/";

    /// <summary>当前选中的文件/目录项（无选中为 null；多选时为首个选中项，T-083 批量动作走事件列表参数）。</summary>
    public FileBrowseItem? SelectedItem { get; private set; }

    /// <summary>当前是否处于搜索模式（搜索框非空）。</summary>
    public bool IsSearchActive => _isSearchActive;

    // ================================================================
    // 控件（internal 供 T-109 外提协作类构建/渲染/交互访问）
    // ================================================================

    internal FlowLayoutPanel _breadcrumbBar = null!;
    internal Button _upButton = null!;
    internal TextBox _searchBox = null!;
    internal Button _listViewButton = null!;
    internal Button _gridViewButton = null!;
    internal Button _deleteButton = null!; // T-014：删除（进回收站）
    internal Button _trashButton = null!;  // T-014：回收站入口
    internal Button _shareButton = null!;  // T-018：分享（仅文件）
    internal Button _versionButton = null!; // T-018：版本历史（仅文件）
    internal Button _uploadButton = null!;   // T-033：上传（多选文件）
    internal Button _downloadButton = null!; // T-033：下载到本机（仅 CloudOnly 选中项）
    internal ComboBox _sortCombo = null!;
    internal ListView _list = null!;
    internal Label _emptyLabel = null!;
    internal ThumbnailLoader _thumbs = null!; // T-087：网格缩略图加载器（自持 ImageList）

    // 右键上下文菜单（T-083，internal 供 FileBrowserContextMenu 构建 + ListMenu_Opening 刷新）
    internal ContextMenuStrip _listMenu = null!;
    internal ToolStripMenuItem _menuOpenItem = null!;
    internal ToolStripMenuItem _menuDownloadItem = null!;
    internal ToolStripMenuItem _menuShareItem = null!;
    internal ToolStripMenuItem _menuVersionItem = null!;
    internal ToolStripMenuItem _menuDeleteItem = null!;

    // ================================================================
    // 状态
    // ================================================================

    internal IReadOnlyList<FileBrowseItem> _currentItems = Array.Empty<FileBrowseItem>();
    internal bool _isSearchActive;
    internal string _sortMode = "名称";
    internal bool _sortAscending = true;
    internal List<string> _selectedPaths = new(); // T-083：多选路径集合（刷新后恢复全部选中项）
    internal bool _syncingSortCombo; // 列点击同步排序下拉时抑制其事件

    // T-109：外提协作类（布局构建/事件分派/列表渲染/菜单/面包屑）
    private readonly FileBrowserListRenderer _renderer;
    private readonly FileBrowserBreadcrumb _breadcrumb;
    private readonly FileBrowserContextMenu _contextMenu;
    private readonly FileBrowserEvents _events;

    // ================================================================
    // 构造与布局
    // ================================================================

    public FileBrowserView()
    {
        _renderer = new FileBrowserListRenderer(this);
        _breadcrumb = new FileBrowserBreadcrumb(this);
        _contextMenu = new FileBrowserContextMenu(this);
        _events = new FileBrowserEvents(this, _renderer);

        FileBrowserLayoutBuilder.Build(this, _renderer, _breadcrumb, _contextMenu);
        WireEvents();

        // T-033：支持拖拽文件到浏览视图即导入同步根（上传入口）。
        // 递归开启全部子控件拖放目标（含空目录时的 _emptyLabel 覆盖层，避免其挡住列表的拖放）。
        EnableDropTarget(this);
    }

    /// <summary>绑定工具栏/列表具名事件处理器（T-109 外提后由视图统一装配，CP301）。</summary>
    private void WireEvents()
    {
        _upButton.Click += _events.UpButton_Click;
        _searchBox.TextChanged += _events.SearchBox_TextChanged;
        _listViewButton.Click += _events.ViewListButton_Click;
        _gridViewButton.Click += _events.ViewGridButton_Click;
        _sortCombo.SelectedIndexChanged += _events.SortCombo_SelectedIndexChanged;
        _list.ColumnClick += _events.List_ColumnClick;
        _list.ItemActivate += _events.List_ItemActivate;
        _list.SelectedIndexChanged += _events.List_SelectedIndexChanged;
        _uploadButton.Click += _events.UploadButton_Click;
        _deleteButton.Click += _events.DeleteButton_Click;
        _trashButton.Click += _events.TrashButton_Click;
        _shareButton.Click += _events.ShareButton_Click;
        _versionButton.Click += _events.VersionButton_Click;
        _downloadButton.Click += _events.DownloadButton_Click;
    }

    /// <summary>递归为控件树开启文件拖放目标（T-033）：拖入文件收集路径后交宿主导入同步根。</summary>
    private void EnableDropTarget(Control root)
    {
        root.AllowDrop = true;
        root.DragEnter += _events.FileBrowser_DragEnter;
        root.DragDrop += _events.FileBrowser_DragDrop;
        foreach (Control child in root.Controls)
        {
            EnableDropTarget(child);
        }
    }

    // ================================================================
    // 对外数据注入
    // ================================================================

    /// <summary>注入当前目录的数据并渲染：更新路径/面包屑，重绘列表与空状态。</summary>
    public void ShowItems(string path, IReadOnlyList<FileBrowseItem> items, string? searchText)
    {
        CurrentPath = path;
        _isSearchActive = !string.IsNullOrWhiteSpace(searchText);
        _currentItems = items;
        _breadcrumb.Rebuild(path);
        _renderer.RenderList();
    }

    /// <summary>清空搜索框（触发 SearchTextChanged，宿主据此退出搜索模式并重载）。</summary>
    public void ClearSearch()
    {
        if (_searchBox.Text.Length > 0)
        {
            _searchBox.Text = "";
        }
    }

    // ================================================================
    // 选中集合与状态同步（T-083，供事件/渲染/菜单协作类复用）
    // ================================================================

    /// <summary>T-083：从 ListView 选中项同步状态——SelectedItem（首个）/批量删除按钮文本与可用性/分享版本仅单选文件/下载仅 CloudOnly 子集。</summary>
    internal void UpdateSelection()
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

    /// <summary>当前 ListView 全部选中项（按选中顺序），Tag 非 FileBrowseItem 时跳过。</summary>
    internal List<FileBrowseItem> GetSelectedItems()
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
    internal List<FileBrowseItem> GetDownloadableSelection()
    {
        return GetSelectedItems()
            .Where(i => !i.IsDirectory && i.State == (int)FileState.CloudOnly && !i.LocalExists)
            .ToList();
    }

    /// <summary>将 FileBrowseItem 映射为（图标, 颜色）双通道；未注入 StateResolver 时用 FileBrowseRender 默认映射。</summary>
    internal (string Icon, Color Color) ResolveState(FileBrowseItem item)
    {
        return StateResolver != null ? StateResolver(item) : FileBrowseRender.ResolveDefaultState(item);
    }

    // ================================================================
    // 右键菜单弹出前可用性刷新（T-083，反射保留入口；构建与点击处理在 FileBrowserContextMenu）
    // ================================================================

    /// <summary>T-083：右键菜单弹出前按当前选中刷新可用性——分享/版本/打开仅单选文件，下载仅 CloudOnly 子集，删除任意选中。</summary>
    internal void ListMenu_Opening(object? sender, CancelEventArgs e)
    {
        List<FileBrowseItem> selected = GetSelectedItems();
        bool singleFile = selected.Count == 1 && !selected[0].IsDirectory;
        _menuOpenItem.Enabled = singleFile;
        _menuDownloadItem.Enabled = GetDownloadableSelection().Count > 0;
        _menuShareItem.Enabled = singleFile;
        _menuVersionItem.Enabled = singleFile;
        _menuDeleteItem.Enabled = selected.Count > 0;
    }
}
