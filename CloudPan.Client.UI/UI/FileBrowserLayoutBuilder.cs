using CloudPan.Infrastructure.Design;

namespace CloudPan.Client.UI;

/// <summary>文件浏览视图布局构建协作类（T-109）：一次性构建面包屑/工具栏/按钮/列表/空状态控件树并赋给视图字段。</summary>
internal static class FileBrowserLayoutBuilder
{
    /// <summary>构建整个控件树并装配菜单/渲染/面包屑协作类（事件绑定由视图 WireEvents 统一完成）。</summary>
    public static void Build(
        FileBrowserView view,
        FileBrowserListRenderer renderer,
        FileBrowserBreadcrumb breadcrumb,
        FileBrowserContextMenu contextMenu)
    {
        var baseFont = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;

        // ── 面包屑行：上一级 + 路径导航 ──
        view._breadcrumbBar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 48,
            Padding = new Padding(8, 2, 8, 2),
            WrapContents = false,
            AutoScroll = true,
            BackColor = CloudPanColors.BackgroundWhite,
        };

        view._upButton = new Button
        {
            Text = "↑ 上一级",
            Width = 96,
            Height = CloudPanSpacing.MinTouchSize,
            FlatStyle = FlatStyle.Flat,
            Margin = new Padding(0, 0, 6, 0),
        };
        view._upButton.FlatAppearance.BorderColor = CloudPanColors.ButtonBorderGray;
        view._breadcrumbBar.Controls.Add(view._upButton);

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
        view._searchBox = new TextBox
        {
            Dock = DockStyle.Fill,
            Font = new Font(baseFont.FontFamily, CloudPanFonts.SizeBody),
            PlaceholderText = "搜索文件…",
        };
        searchWrap.Controls.Add(view._searchBox);
        toolbar.Controls.Add(searchWrap, 0, 0);

        FlowLayoutPanel viewPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(0, 5, 0, 0),
        };
        int toggleW = 64;
        view._listViewButton = new Button
        {
            Text = "列表",
            Width = toggleW,
            Height = CloudPanSpacing.MinTouchSize,
            FlatStyle = FlatStyle.Flat,
        };
        view._listViewButton.FlatAppearance.BorderColor = CloudPanColors.ButtonBorderGray;
        view._gridViewButton = new Button
        {
            Text = "网格",
            Width = toggleW,
            Height = CloudPanSpacing.MinTouchSize,
            FlatStyle = FlatStyle.Flat,
            Margin = new Padding(4, 0, 0, 0),
        };
        view._gridViewButton.FlatAppearance.BorderColor = CloudPanColors.ButtonBorderGray;
        viewPanel.Controls.Add(view._listViewButton);
        viewPanel.Controls.Add(view._gridViewButton);

        // T-033：上传（多选文件，复制到当前浏览目录并入队上传）
        view._uploadButton = new Button
        {
            Text = "上传",
            Width = 64,
            Height = CloudPanSpacing.MinTouchSize,
            FlatStyle = FlatStyle.Flat,
            Margin = new Padding(4, 0, 0, 0),
        };
        view._uploadButton.FlatAppearance.BorderColor = CloudPanColors.ButtonBorderGray;
        viewPanel.Controls.Add(view._uploadButton);

        // T-014：删除（进回收站，无选中项禁用；T-083 多选时文本变「批量删除」）+ 回收站入口
        view._deleteButton = new Button
        {
            Text = "删除",
            Width = 88,
            Height = CloudPanSpacing.MinTouchSize,
            FlatStyle = FlatStyle.Flat,
            Margin = new Padding(4, 0, 0, 0),
            Enabled = false,
        };
        view._deleteButton.FlatAppearance.BorderColor = CloudPanColors.ButtonBorderGray;
        viewPanel.Controls.Add(view._deleteButton);

        view._trashButton = new Button
        {
            Text = "回收站",
            Width = 76,
            Height = CloudPanSpacing.MinTouchSize,
            FlatStyle = FlatStyle.Flat,
            Margin = new Padding(4, 0, 0, 0),
        };
        view._trashButton.FlatAppearance.BorderColor = CloudPanColors.ButtonBorderGray;
        viewPanel.Controls.Add(view._trashButton);

        // T-018：分享 + 版本历史（仅对选中文件可用，目录/未选中禁用）
        view._shareButton = new Button
        {
            Text = "分享",
            Width = 64,
            Height = CloudPanSpacing.MinTouchSize,
            FlatStyle = FlatStyle.Flat,
            Margin = new Padding(4, 0, 0, 0),
            Enabled = false,
        };
        view._shareButton.FlatAppearance.BorderColor = CloudPanColors.ButtonBorderGray;
        viewPanel.Controls.Add(view._shareButton);

        view._versionButton = new Button
        {
            Text = "版本",
            Width = 64,
            Height = CloudPanSpacing.MinTouchSize,
            FlatStyle = FlatStyle.Flat,
            Margin = new Padding(4, 0, 0, 0),
            Enabled = false,
        };
        view._versionButton.FlatAppearance.BorderColor = CloudPanColors.ButtonBorderGray;
        viewPanel.Controls.Add(view._versionButton);

        // T-033：下载到本机（仅 CloudOnly 选中项可用，按需取回）
        view._downloadButton = new Button
        {
            Text = "下载到本机",
            Width = 88,
            Height = CloudPanSpacing.MinTouchSize,
            FlatStyle = FlatStyle.Flat,
            Margin = new Padding(4, 0, 0, 0),
            Enabled = false,
        };
        view._downloadButton.FlatAppearance.BorderColor = CloudPanColors.ButtonBorderGray;
        viewPanel.Controls.Add(view._downloadButton);

        toolbar.Controls.Add(viewPanel, 1, 0);

        view._sortCombo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Dock = DockStyle.Fill,
            Width = 92,
            Font = new Font(baseFont.FontFamily, CloudPanFonts.SizeBody),
            Margin = new Padding(8, 7, 0, 0),
        };
        view._sortCombo.Items.AddRange(new object[] { "名称", "大小", "类型" });
        view._sortCombo.SelectedIndex = 0;
        toolbar.Controls.Add(view._sortCombo, 2, 0);

        // ── 文件列表（网格缩略图 ImageList 由 ThumbnailLoader 自持并绑定 LargeImageList，T-087）──
        view._list = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = true, // T-083：Ctrl/Shift 多选 → 批量删除/下载
            HideSelection = false,
            HeaderStyle = ColumnHeaderStyle.Clickable,
            BorderStyle = BorderStyle.None,
            BackColor = CloudPanColors.BackgroundWhite,
            ForeColor = CloudPanColors.TextPrimary,
            Font = new Font(baseFont.FontFamily, CloudPanFonts.SizeBody),
        };
        view._thumbs = new ThumbnailLoader(view._list); // T-087：网格缩略图加载器（含文件夹/文件字形索引 0/1）
        view._list.Columns.Add("状态", 70);
        view._list.Columns.Add("名称", 320);
        view._list.Columns.Add("大小", 90);
        view._list.Columns.Add("类型", 90);
        contextMenu.Build(); // T-083：右键上下文菜单（下载/分享/删除/版本历史/打开）

        view._emptyLabel = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font(baseFont.FontFamily, CloudPanFonts.SizeBody),
            ForeColor = CloudPanColors.TextMuted,
            BackColor = CloudPanColors.BackgroundWhite,
            Visible = false,
        };

        // z-order：列表最底层，空状态标签覆盖其上，工具栏/面包屑在上
        view.Controls.Add(view._list);
        view.Controls.Add(view._emptyLabel);
        view.Controls.Add(toolbar);
        view.Controls.Add(view._breadcrumbBar);

        renderer.UpdateViewToggle();
        breadcrumb.Rebuild("/");
    }
}
