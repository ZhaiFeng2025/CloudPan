using System.Drawing.Drawing2D;
using CloudPan.Client.Services;
using CloudPan.Shared;

namespace CloudPan.Client.UI;

/// <summary>
/// 文件浏览主视图（T-013）——纯渲染控件：面包屑导航 + 上一级 + 搜索 + 列表/网格切换 + 排序 + 每文件同步状态图标。
/// 数据由宿主（MainWindow）经 SyncEngine 加载后通过 <see cref="ShowItems"/> 注入，本控件只渲染与交互，不做数据访问。
/// </summary>
public class FileBrowserView : UserControl
{
    // ================================================================
    // 对外事件（由宿主处理导航/数据加载）
    // ================================================================

    /// <summary>目录激活（双击子目录 / 点击面包屑段）→ 参数为目录相对路径。</summary>
    public event Action<string>? DirectoryActivated;

    /// <summary>文件激活（双击文件）→ 参数为文件相对路径。</summary>
    public event Action<string>? FileActivated;

    /// <summary>点击「上一级」。</summary>
    public event Action? UpRequested;

    /// <summary>搜索框内容变化 → 参数为当前搜索文本（可能为空串）。</summary>
    public event Action<string>? SearchTextChanged;

    /// <summary>状态解析器（由宿主注入，叠加本地错误/冲突覆盖）。未注入时使用默认 FileState → 图标/颜色映射。</summary>
    public Func<FileBrowseItem, (string Icon, Color Color)>? StateResolver { get; set; }

    /// <summary>当前浏览的目录相对路径（"/" 为根）。</summary>
    public string CurrentPath { get; private set; } = "/";

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
            return;
        }

        foreach (ListViewItem lvi in _list.Items)
        {
            if (lvi.Tag is FileBrowseItem item && string.Equals(item.Path, _selectedPath, StringComparison.OrdinalIgnoreCase))
            {
                lvi.Selected = true;
                lvi.EnsureVisible();
                break;
            }
        }
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
    // 面包屑
    // ================================================================

    /// <summary>重建面包屑导航：仅保留「上一级」按钮，按当前路径生成可点击的路径段。</summary>
    private void RebuildBreadcrumb(string path)
    {
        _breadcrumbBar.SuspendLayout();
        // 移除并释放「上一级」之外的全部动态面包屑控件（索引 ≥1）
        for (int i = _breadcrumbBar.Controls.Count - 1; i >= 1; i--)
        {
            Control c = _breadcrumbBar.Controls[i];
            _breadcrumbBar.Controls.RemoveAt(i);
            c.Dispose();
        }

        _breadcrumbBar.Controls.Add(_upButton);

        AddBreadcrumbLink("主目录", "/");
        string p = path.Trim('/');
        if (p.Length > 0)
        {
            string acc = "";
            foreach (string seg in p.Split('/'))
            {
                acc += "/" + seg;
                _breadcrumbBar.Controls.Add(CreateBreadcrumbSeparator());
                AddBreadcrumbLink(seg, acc);
            }
        }

        _breadcrumbBar.ResumeLayout();
    }

    /// <summary>添加一个可点击的面包屑段（Tag 存目标路径）。</summary>
    private void AddBreadcrumbLink(string text, string path)
    {
        Button link = new Button
        {
            Text = text,
            Tag = path,
            AutoSize = true,
            Height = CloudPanSpacing.MinTouchSize,
            FlatStyle = FlatStyle.Flat,
        };
        link.FlatAppearance.BorderColor = CloudPanColors.BorderLight;
        link.Click += BreadcrumbButton_Click;
        _breadcrumbBar.Controls.Add(link);
    }

    /// <summary>创建面包屑段之间的分隔符。</summary>
    private static Label CreateBreadcrumbSeparator()
    {
        return new Label
        {
            Text = "›",
            AutoSize = true,
            TextAlign = ContentAlignment.MiddleCenter,
            Height = CloudPanSpacing.MinTouchSize,
            Margin = new Padding(2, 0, 2, 0),
            ForeColor = CloudPanColors.TextMuted,
            Font = new Font((SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont).FontFamily, 12f),
        };
    }

    // ================================================================
    // 事件处理（具名方法，CP301）
    // ================================================================

    private void UpButton_Click(object? sender, EventArgs e) => UpRequested?.Invoke();

    private void SearchBox_TextChanged(object? sender, EventArgs e) => SearchTextChanged?.Invoke(_searchBox.Text);

    private void BreadcrumbButton_Click(object? sender, EventArgs e)
    {
        if (sender is Button btn && btn.Tag is string path)
        {
            DirectoryActivated?.Invoke(path);
        }
    }

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
        if (_list.SelectedItems.Count > 0 && _list.SelectedItems[0].Tag is FileBrowseItem item)
        {
            _selectedPath = item.Path;
        }
    }

    private void List_ItemActivate(object? sender, EventArgs e)
    {
        if (_list.SelectedItems.Count == 0 || _list.SelectedItems[0].Tag is not FileBrowseItem item)
        {
            return;
        }

        _selectedPath = item.Path;
        if (item.IsDirectory)
        {
            DirectoryActivated?.Invoke(item.Path);
        }
        else
        {
            FileActivated?.Invoke(item.Path);
        }
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
