using CloudPan.Client.Core.Services;
using CloudPan.Contract;
using CloudPan.Infrastructure.Design;

namespace CloudPan.Client.UI;

/// <summary>
/// 文件浏览主视图（T-013）——纯渲染控件：面包屑导航 + 上一级 + 搜索 + 列表/网格切换 + 排序 + 每文件同步状态图标。
/// 数据由宿主（MainWindow）经 SyncEngine 加载后通过 <see cref="ShowItems"/> 注入，本控件只渲染与交互，不做数据访问。
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

    /// <summary>点击「下载到本机」（选中 CloudOnly 文件）→ 参数为选中的文件（T-033，宿主调用 DownloadPathAsync）。</summary>
    public event Action<FileBrowseItem>? DownloadRequested;

    /// <summary>拖拽文件到浏览视图 → 参数为拖入的本地文件路径数组（T-033，宿主复制入同步根并入队上传）。</summary>
    public event Action<string[]>? FilesDropped;

    /// <summary>点击「上一级」。</summary>
    public event Action? UpRequested;

    /// <summary>搜索框内容变化 → 参数为当前搜索文本（可能为空串）。</summary>
    public event Action<string>? SearchTextChanged;

    /// <summary>点击「删除」（有选中项）→ 参数为选中的文件/目录（T-014，宿主负责进回收站）。</summary>
    public event Action<FileBrowseItem>? DeleteRequested;

    /// <summary>点击「回收站」→ 打开最近删除入口（T-014）。</summary>
    public event Action? TrashRequested;

    /// <summary>点击「分享」（有选中文件）→ 参数为选中的文件（T-018，宿主负责创建/撤销分享）。</summary>
    public event Action<FileBrowseItem>? ShareRequested;

    /// <summary>点击「版本」（有选中文件）→ 参数为选中的文件（T-018，宿主负责版本历史列表/回滚）。</summary>
    public event Action<FileBrowseItem>? VersionHistoryRequested;

    /// <summary>状态解析器（由宿主注入，叠加本地错误/冲突覆盖）。未注入时使用默认 FileState → 图标/颜色映射。</summary>
    public Func<FileBrowseItem, (string Icon, Color Color)>? StateResolver { get; set; }

    /// <summary>当前浏览的目录相对路径（"/" 为根）。</summary>
    public string CurrentPath { get; private set; } = "/";

    /// <summary>当前选中的文件/目录项（无选中为 null，T-014 删除操作依据）。</summary>
    public FileBrowseItem? SelectedItem { get; private set; }

    /// <summary>当前是否处于搜索模式（搜索框非空）。</summary>
    public bool IsSearchActive => _isSearchActive;

    // ================================================================
    // 控件
    // ================================================================

    private FlowLayoutPanel _breadcrumbBar = null!;
    private Button _upButton = null!;
    private TextBox _searchBox = null!;
    private Button _listViewButton = null!;
    private Button _gridViewButton = null!;
    private Button _deleteButton = null!; // T-014：删除（进回收站）
    private Button _trashButton = null!;  // T-014：回收站入口
    private Button _shareButton = null!;  // T-018：分享（仅文件）
    private Button _versionButton = null!; // T-018：版本历史（仅文件）
    private Button _uploadButton = null!;   // T-033：上传（多选文件）
    private Button _downloadButton = null!; // T-033：下载到本机（仅 CloudOnly 选中项）
    private ComboBox _sortCombo = null!;
    private ListView _list = null!;
    private Label _emptyLabel = null!;
    private ImageList _glyphImages = null!;

    // ================================================================
    // 状态
    // ================================================================

    private IReadOnlyList<FileBrowseItem> _currentItems = Array.Empty<FileBrowseItem>();
    private bool _isSearchActive;
    private string _sortMode = "名称";
    private bool _sortAscending = true;
    private string? _selectedPath;
    private bool _syncingSortCombo; // 列点击同步排序下拉时抑制其事件

    // ================================================================
    // 构造与布局
    // ================================================================

    public FileBrowserView()
    {
        BuildLayout();

        // T-033：支持拖拽文件到浏览视图即导入同步根（上传入口）。
        // 递归开启全部子控件拖放目标（含空目录时的 _emptyLabel 覆盖层，避免其挡住列表的拖放）。
        EnableDropTarget(this);
    }

    /// <summary>递归为控件树开启文件拖放目标（T-033）：拖入文件收集路径后交宿主导入同步根。</summary>
    private void EnableDropTarget(Control root)
    {
        root.AllowDrop = true;
        root.DragEnter += FileBrowser_DragEnter;
        root.DragDrop += FileBrowser_DragDrop;
        foreach (Control child in root.Controls)
        {
            EnableDropTarget(child);
        }
    }

    private void BuildLayout()
    {
        var baseFont = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;

        // ── 面包屑行：上一级 + 路径导航 ──
        _breadcrumbBar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 48,
            Padding = new Padding(8, 2, 8, 2),
            WrapContents = false,
            AutoScroll = true,
            BackColor = CloudPanColors.BackgroundWhite,
        };

        _upButton = new Button
        {
            Text = "↑ 上一级",
            Width = 96,
            Height = CloudPanSpacing.MinTouchSize,
            FlatStyle = FlatStyle.Flat,
            Margin = new Padding(0, 0, 6, 0),
        };
        _upButton.FlatAppearance.BorderColor = CloudPanColors.ButtonBorderGray;
        _upButton.Click += UpButton_Click;
        _breadcrumbBar.Controls.Add(_upButton);

        // ── 工具栏行：搜索 + 视图切换 + 排序 ──
        TableLayoutPanel toolbar = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 48,
            Padding = new Padding(8, 2, 8, 2),
            BackColor = CloudPanColors.BackgroundLight,
        };
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        toolbar.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));

        Panel searchWrap = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(0, 6, 8, 6),
            BackColor = CloudPanColors.BackgroundLight,
        };
        _searchBox = new TextBox
        {
            Dock = DockStyle.Fill,
            Font = new Font(baseFont.FontFamily, CloudPanFonts.SizeBody),
            PlaceholderText = "搜索文件…",
        };
        _searchBox.TextChanged += SearchBox_TextChanged;
        searchWrap.Controls.Add(_searchBox);
        toolbar.Controls.Add(searchWrap, 0, 0);

        FlowLayoutPanel viewPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(0, 5, 0, 0),
        };
        int toggleW = 64;
        _listViewButton = new Button
        {
            Text = "列表",
            Width = toggleW,
            Height = CloudPanSpacing.MinTouchSize,
            FlatStyle = FlatStyle.Flat,
        };
        _listViewButton.FlatAppearance.BorderColor = CloudPanColors.ButtonBorderGray;
        _listViewButton.Click += ViewListButton_Click;
        _gridViewButton = new Button
        {
            Text = "网格",
            Width = toggleW,
            Height = CloudPanSpacing.MinTouchSize,
            FlatStyle = FlatStyle.Flat,
            Margin = new Padding(4, 0, 0, 0),
        };
        _gridViewButton.FlatAppearance.BorderColor = CloudPanColors.ButtonBorderGray;
        _gridViewButton.Click += ViewGridButton_Click;
        viewPanel.Controls.Add(_listViewButton);
        viewPanel.Controls.Add(_gridViewButton);

        // T-033：上传（多选文件，复制到当前浏览目录并入队上传）
        _uploadButton = new Button
        {
            Text = "上传",
            Width = 64,
            Height = CloudPanSpacing.MinTouchSize,
            FlatStyle = FlatStyle.Flat,
            Margin = new Padding(4, 0, 0, 0),
        };
        _uploadButton.FlatAppearance.BorderColor = CloudPanColors.ButtonBorderGray;
        _uploadButton.Click += UploadButton_Click;
        viewPanel.Controls.Add(_uploadButton);

        // T-014：删除（进回收站，无选中项禁用）+ 回收站入口
        _deleteButton = new Button
        {
            Text = "删除",
            Width = 64,
            Height = CloudPanSpacing.MinTouchSize,
            FlatStyle = FlatStyle.Flat,
            Margin = new Padding(4, 0, 0, 0),
            Enabled = false,
        };
        _deleteButton.FlatAppearance.BorderColor = CloudPanColors.ButtonBorderGray;
        _deleteButton.Click += DeleteButton_Click;
        viewPanel.Controls.Add(_deleteButton);

        _trashButton = new Button
        {
            Text = "回收站",
            Width = 76,
            Height = CloudPanSpacing.MinTouchSize,
            FlatStyle = FlatStyle.Flat,
            Margin = new Padding(4, 0, 0, 0),
        };
        _trashButton.FlatAppearance.BorderColor = CloudPanColors.ButtonBorderGray;
        _trashButton.Click += TrashButton_Click;
        viewPanel.Controls.Add(_trashButton);

        // T-018：分享 + 版本历史（仅对选中文件可用，目录/未选中禁用）
        _shareButton = new Button
        {
            Text = "分享",
            Width = 64,
            Height = CloudPanSpacing.MinTouchSize,
            FlatStyle = FlatStyle.Flat,
            Margin = new Padding(4, 0, 0, 0),
            Enabled = false,
        };
        _shareButton.FlatAppearance.BorderColor = CloudPanColors.ButtonBorderGray;
        _shareButton.Click += ShareButton_Click;
        viewPanel.Controls.Add(_shareButton);

        _versionButton = new Button
        {
            Text = "版本",
            Width = 64,
            Height = CloudPanSpacing.MinTouchSize,
            FlatStyle = FlatStyle.Flat,
            Margin = new Padding(4, 0, 0, 0),
            Enabled = false,
        };
        _versionButton.FlatAppearance.BorderColor = CloudPanColors.ButtonBorderGray;
        _versionButton.Click += VersionButton_Click;
        viewPanel.Controls.Add(_versionButton);

        // T-033：下载到本机（仅 CloudOnly 选中项可用，按需取回）
        _downloadButton = new Button
        {
            Text = "下载到本机",
            Width = 88,
            Height = CloudPanSpacing.MinTouchSize,
            FlatStyle = FlatStyle.Flat,
            Margin = new Padding(4, 0, 0, 0),
            Enabled = false,
        };
        _downloadButton.FlatAppearance.BorderColor = CloudPanColors.ButtonBorderGray;
        _downloadButton.Click += DownloadButton_Click;
        viewPanel.Controls.Add(_downloadButton);

        toolbar.Controls.Add(viewPanel, 1, 0);

        _sortCombo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Dock = DockStyle.Fill,
            Width = 92,
            Font = new Font(baseFont.FontFamily, CloudPanFonts.SizeBody),
            Margin = new Padding(8, 7, 0, 0),
        };
        _sortCombo.Items.AddRange(new object[] { "名称", "大小", "类型" });
        _sortCombo.SelectedIndex = 0;
        _sortCombo.SelectedIndexChanged += SortCombo_SelectedIndexChanged;
        toolbar.Controls.Add(_sortCombo, 2, 0);

        // ── 文件列表 ──
        _glyphImages = new ImageList { ColorDepth = ColorDepth.Depth32Bit, ImageSize = new Size(40, 40) };
        _glyphImages.Images.Add(DrawFolderGlyph()); // 0 文件夹
        _glyphImages.Images.Add(DrawFileGlyph());   // 1 文件

        _list = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            HideSelection = false,
            HeaderStyle = ColumnHeaderStyle.Clickable,
            BorderStyle = BorderStyle.None,
            BackColor = CloudPanColors.BackgroundWhite,
            ForeColor = CloudPanColors.TextPrimary,
            Font = new Font(baseFont.FontFamily, CloudPanFonts.SizeBody),
            LargeImageList = _glyphImages,
        };
        _list.Columns.Add("状态", 70);
        _list.Columns.Add("名称", 320);
        _list.Columns.Add("大小", 90);
        _list.Columns.Add("类型", 90);
        _list.ColumnClick += List_ColumnClick;
        _list.ItemActivate += List_ItemActivate;
        _list.SelectedIndexChanged += List_SelectedIndexChanged;

        _emptyLabel = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font(baseFont.FontFamily, CloudPanFonts.SizeBody),
            ForeColor = CloudPanColors.TextMuted,
            BackColor = CloudPanColors.BackgroundWhite,
            Visible = false,
        };

        // z-order：列表最底层，空状态标签覆盖其上，工具栏/面包屑在上
        Controls.Add(_list);
        Controls.Add(_emptyLabel);
        Controls.Add(toolbar);
        Controls.Add(_breadcrumbBar);

        UpdateViewToggle();
        RebuildBreadcrumb("/");
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
        RebuildBreadcrumb(path);
        RenderList();
    }

    /// <summary>清空搜索框（触发 SearchTextChanged，宿主据此退出搜索模式并重载）。</summary>
    public void ClearSearch()
    {
        if (_searchBox.Text.Length > 0)
        {
            _searchBox.Text = "";
        }
    }
}
