using CloudPan.Infrastructure.Design;

namespace CloudPan.Client.UI;

/// <summary>
/// 选择性同步设置面板——显示远程文件夹树，默认全选，取消勾选=排除该子树（排除集语义，T-047）。
/// </summary>
public class SelectiveSyncPanel : UserControl
{
    private readonly TreeView _tree;
    private readonly Button _selectAllBtn;
    private readonly Button _deselectAllBtn;
    private readonly Label _hintLabel;
    private readonly Panel _emptyState;
    private readonly Label _emptyLabel;
    private List<string> _selectedPaths = new() { "/" };

    // T-074：目录树加载状态——树未加载/失败/服务端无目录时保存不得用空树全选覆盖既有排除配置
    private bool _treeLoaded;
    private string? _loadError;

    /// <summary>目录树是否已成功填充（false = 未加载/失败/服务端无目录，保存不得用勾选态覆盖既有排除配置）。</summary>
    public bool IsTreeLoaded => _treeLoaded;

    /// <summary>树未加载的原因提示（供设置页提示与保存阻止文案）。</summary>
    public string? TreeLoadMessage => _loadError;

    /// <summary>
    /// 当前排除路径列表（排除集语义，T-047，以 / 开头，目录以 / 结尾）。
    /// 空集合 = 显式全不同步（取消全选后不再回退为 { "/" } 全选）；含 "/" = 全选（默认/旧版兼容）；其余 = 未勾选排除子树。
    /// </summary>
    public List<string> SelectedPaths
    {
        get
        {
            // 树未填充（未加载/失败/服务端无目录）：返回既有配置（setter 注入），
            // 不静默回退 { "/" } 全选覆盖用户排除集（T-074）
            if (_tree.Nodes.Count == 0)
            {
                return new List<string>(_selectedPaths);
            }

            List<string> excluded = new List<string>();
            TreeNode root = _tree.Nodes[0];
            if (IsFullyChecked(root))
            {
                // 全选：返回 "/" 全选默认值（引擎识别为全选）
                excluded.Add("/");
            }
            else if (!root.Checked)
            {
                // root 未勾选（子节点已跟随传播全部未勾选）= 全不同步：返回空集合
                return excluded;
            }
            else
            {
                // 部分勾选：收集顶层未勾选子树（子节点被父前缀覆盖，无需逐个收集）
                CollectUnchecked(_tree.Nodes, excluded);
            }
            return excluded;
        }
        set
        {
            _selectedPaths = value ?? new List<string> { "/" };
            ApplySelections();
        }
    }

    public SelectiveSyncPanel()
    {
        Dock = DockStyle.Fill;
        Font = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;

        _hintLabel = new Label { Text = "选择要同步的文件夹（未选中的文件夹不会下载到本机）", Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 0, 0, 8) };

        _selectAllBtn = new Button
        {
            Text = "全选",
            Width = 70,
            FlatStyle = FlatStyle.Flat,
            BackColor = CloudPanColors.BackgroundLight,
            ForeColor = CloudPanColors.TextSecondary,
            Cursor = Cursors.Hand,
            UseVisualStyleBackColor = false,
        };
        _selectAllBtn.FlatAppearance.BorderColor = CloudPanColors.BorderLight;
        _selectAllBtn.FlatAppearance.MouseOverBackColor = CloudPanColors.ButtonHoverBg;
        _selectAllBtn.FlatAppearance.MouseDownBackColor = CloudPanColors.ButtonPressBg;

        _deselectAllBtn = new Button
        {
            Text = "取消全选",
            Width = 70,
            FlatStyle = FlatStyle.Flat,
            BackColor = CloudPanColors.BackgroundLight,
            ForeColor = CloudPanColors.TextSecondary,
            Cursor = Cursors.Hand,
            UseVisualStyleBackColor = false,
        };
        _deselectAllBtn.FlatAppearance.BorderColor = CloudPanColors.BorderLight;
        _deselectAllBtn.FlatAppearance.MouseOverBackColor = CloudPanColors.ButtonHoverBg;
        _deselectAllBtn.FlatAppearance.MouseDownBackColor = CloudPanColors.ButtonPressBg;
        _selectAllBtn.Click += SelectAllBtn_Click;
        _deselectAllBtn.Click += DeselectAllBtn_Click;

        FlowLayoutPanel btnRow = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.LeftToRight, Height = 36 };
        btnRow.Controls.Add(_selectAllBtn);
        btnRow.Controls.Add(_deselectAllBtn);

        _tree = new TreeView { Dock = DockStyle.Fill, CheckBoxes = true, Visible = false };
        _tree.AfterCheck += OnNodeChecked;

        // 空状态面板——树为空时显示提示，避免用户看到空白面板不知所措
        _emptyState = new Panel { Dock = DockStyle.Fill, BackColor = CloudPanColors.BackgroundWhite };
        _emptyLabel = new Label
        {
            Text = "尚未从服务端加载目录列表。\n连接服务端后此功能将自动生效。\n\n当前设置：同步根目录下所有文件。",
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = CloudPanColors.TextSecondary,
            Dock = DockStyle.Fill,
            Padding = new Padding(20),
            Font = new Font(SystemFonts.MessageBoxFont?.FontFamily ?? FontFamily.GenericSansSerif, 9.5F),
        };
        _emptyState.Controls.Add(_emptyLabel);

        Controls.Add(_tree);
        Controls.Add(_emptyState);
        Controls.Add(btnRow);
        Controls.Add(_hintLabel);
        btnRow.BringToFront();
    }

    /// <summary>标记目录树加载中（设置页异步加载开始时调用）。</summary>
    public void SetLoading()
    {
        _treeLoaded = false;
        _loadError = "正在加载服务端目录列表...";
        UpdateEmptyState();
    }

    /// <summary>标记目录树加载失败/为空（未填充任何目录），UI 据此禁用保存并提示，避免保存静默覆盖既有排除配置。</summary>
    public void SetLoadFailed(string message)
    {
        _treeLoaded = false;
        _loadError = message;
        UpdateEmptyState();
    }

    private void UpdateEmptyState()
    {
        _tree.Visible = false;
        _emptyState.Visible = true;
        _emptyLabel.Text = _loadError ?? "尚未从服务端加载目录列表。\n连接服务端后此功能将自动生效。\n\n当前设置：同步根目录下所有文件。";
    }

    /// <summary>从远程文件树加载目录结构。加载成功后面板切换到目录树显示。</summary>
    public void LoadFromPaths(IEnumerable<string> remotePaths)
    {
        _tree.Nodes.Clear();
        TreeNode root = new TreeNode("CloudPan（根目录）") { Tag = "/", Checked = true };
        _tree.Nodes.Add(root);

        List<string> dirs = remotePaths
            .Where(p => p.EndsWith("/"))
            .Select(p => p.TrimEnd('/'))
            .Where(p => !string.IsNullOrEmpty(p))
            .OrderBy(p => p)
            .Distinct()
            .ToList();

        foreach (string? dir in dirs)
        {
            string[] parts = dir.Split('/');
            var parent = root;
            string currentPath = "";
            foreach (string part in parts)
            {
                if (string.IsNullOrEmpty(part))
                {
                    continue;
                }

                currentPath += "/" + part;
                var existing = parent.Nodes.Cast<TreeNode>().FirstOrDefault(n => (string?)n.Tag == currentPath);
                if (existing != null)
                {
                    parent = existing;
                }
                else
                {
                    TreeNode node = new TreeNode(part) { Tag = currentPath + "/", Checked = true };
                    parent.Nodes.Add(node);
                    parent = node;
                }
            }
        }

        root.ExpandAll();
        _tree.Visible = true;
        _emptyState.Visible = false;
        _treeLoaded = true;
        _loadError = null;
        ApplySelections();
    }

    private void SetAll(bool check)
    {
        SetAllNodes(_tree.Nodes, check);
    }

    private void SelectAllBtn_Click(object? sender, EventArgs e) => SetAll(true);

    private void DeselectAllBtn_Click(object? sender, EventArgs e) => SetAll(false);

    private static void SetAllNodes(TreeNodeCollection nodes, bool check)
    {
        foreach (TreeNode node in nodes)
        {
            node.Checked = check;
            SetAllNodes(node.Nodes, check);
        }
    }

    private void OnNodeChecked(object? sender, TreeViewEventArgs e)
    {
        // 子节点跟随父节点状态
        if (e.Node != null && (e.Action == TreeViewAction.ByMouse || e.Action == TreeViewAction.ByKeyboard))
        {
            SetChildrenChecked(e.Node, e.Node.Checked);
        }
    }

    private static void SetChildrenChecked(TreeNode parent, bool check)
    {
        foreach (TreeNode child in parent.Nodes)
        {
            child.Checked = check;
            SetChildrenChecked(child, check);
        }
    }

    private void ApplySelections()
    {
        foreach (TreeNode node in _tree.Nodes)
        {
            ApplySelection(node, _selectedPaths);
        }
    }

    private static void ApplySelection(TreeNode node, List<string> paths)
    {
        string? path = node.Tag as string;
        if (path != null)
        {
            if (paths.Count == 0)
            {
                // 空集合 = 显式全不同步：全部取消勾选
                node.Checked = false;
            }
            else if (paths.Contains("/"))
            {
                // 含 "/"（全选默认 / v1.0.0 旧版选择集恒含根节点）→ 全选
                node.Checked = true;
            }
            else
            {
                // 排除集：命中任一排除子树（含深层路径）→ 取消勾选
                bool excluded = paths.Any(p =>
                    path.TrimEnd('/') == p.TrimEnd('/')
                    || path.StartsWith(p.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase));
                node.Checked = !excluded;
            }
        }
        foreach (TreeNode child in node.Nodes)
        {
            ApplySelection(child, paths);
        }
    }

    /// <summary>节点及其全部子节点是否均已勾选（用于判定全选态）。</summary>
    private static bool IsFullyChecked(TreeNode node) =>
        node.Checked && node.Nodes.Cast<TreeNode>().All(IsFullyChecked);

    /// <summary>收集顶层未勾选节点路径（排除子树）。子节点已随父未勾选传播全部取消，由父前缀覆盖，无需逐个收集。</summary>
    private static void CollectUnchecked(TreeNodeCollection nodes, List<string> paths)
    {
        foreach (TreeNode node in nodes)
        {
            if (node.Checked)
            {
                // 父勾选 → 深入收集未勾选子节点
                CollectUnchecked(node.Nodes, paths);
            }
            else if (node.Tag is string path)
            {
                paths.Add(path);
            }
        }
    }
}
