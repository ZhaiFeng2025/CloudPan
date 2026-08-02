using CloudPan.Shared;

namespace CloudPan.Client.UI;

/// <summary>
/// 选择性同步设置面板——显示远程文件夹树，用户勾选需要同步的子目录。
/// </summary>
public class SelectiveSyncPanel : UserControl
{
    private readonly TreeView _tree;
    private readonly Button _selectAllBtn;
    private readonly Button _deselectAllBtn;
    private readonly Label _hintLabel;
    private List<string> _selectedPaths = new() { "/" };

    /// <summary>当前选中的路径列表（以 / 开头，目录以 / 结尾）。</summary>
    public List<string> SelectedPaths
    {
        get
        {
            List<string> paths = new List<string>();
            CollectChecked(_tree.Nodes, paths);
            return paths.Count == 0 ? new List<string> { "/" } : paths;
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
        Panel emptyState = new Panel { Dock = DockStyle.Fill, BackColor = Color.White };
        Label emptyLabel = new Label
        {
            Text = "尚未从服务端加载目录列表。\n连接服务端后此功能将自动生效。\n\n当前设置：同步根目录下所有文件。",
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.Gray,
            Dock = DockStyle.Fill,
            Padding = new Padding(20),
            Font = new Font(SystemFonts.MessageBoxFont?.FontFamily ?? FontFamily.GenericSansSerif, 9.5F),
        };
        emptyState.Controls.Add(emptyLabel);

        Controls.Add(_tree);
        Controls.Add(emptyState);
        Controls.Add(btnRow);
        Controls.Add(_hintLabel);
        btnRow.BringToFront();
    }

    /// <summary>从远程文件树加载目录结构。</summary>
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
            node.Checked = paths.Any(p =>
                p.TrimEnd('/') == path.TrimEnd('/') ||
                path.StartsWith(p.TrimEnd('/') + "/"));
        }
        foreach (TreeNode child in node.Nodes)
        {
            ApplySelection(child, paths);
        }
    }

    private static void CollectChecked(TreeNodeCollection nodes, List<string> paths)
    {
        foreach (TreeNode node in nodes)
        {
            if (node.Checked && node.Tag is string path)
            {
                paths.Add(path);
            }

            CollectChecked(node.Nodes, paths);
        }
    }
}
