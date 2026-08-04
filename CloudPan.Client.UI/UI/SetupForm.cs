using CloudPan.Infrastructure.Design;

namespace CloudPan.Client.UI;

/// <summary>
/// 配置窗口——支持局域网自动发现服务端，输入校验与视觉反馈。
/// 引导步骤（布局/校验/发现）外提为 SetupWizardLayout/SetupWizardValidation/SetupWizardDiscovery 协作类（T-109）。
/// </summary>
public partial class SetupForm : Form
{
    internal const int FieldMargin = CloudPanSpacing.MarginStandard;

    // 输入控件
    internal readonly TextBox _serverUrlBox;
    internal readonly TextBox _syncRootBox;
    internal readonly TextBox _tokenBox;

    // 按钮
    internal readonly Button _searchButton;
    internal readonly Button _browseButton;
    internal readonly Button _tokenToggleBtn;
    internal Button _okButton = null!;

    // 状态指示
    internal readonly Label _statusLabel;
    internal readonly Label _urlStatusIcon;
    internal readonly ProgressBar _progressBar;

    // 字段级提示（深灰 = 信息性提示，红色 = 阻止提交的错误）
    internal readonly Label _urlErrorLabel;
    internal readonly Label _folderErrorLabel;
    internal readonly Label _tokenErrorLabel;

    // Token 下方提示（替代占位符覆盖层）
    internal readonly Label _tokenHintLabel;

    // 搜索按钮旋转动画 + 搜索状态标记
    internal readonly System.Windows.Forms.Timer _searchAnimTimer;
    internal int _searchAnimFrame;
    internal bool _isSearching;
    internal bool _searchFound;  // 搜索成功后设置，防止 TextChanged 重置状态图标

    private bool _tokenMasked = true;

    // T-109：引导职责外提协作类（布局/校验/发现），只持表单引用惰性访问控件
    private readonly SetupWizardLayout _layout;
    private readonly SetupWizardValidation _validation;
    private readonly SetupWizardDiscovery _discovery;

    // ─── 公共属性（供 Program.cs 读取） ───────────────────────────
    public string ServerUrl => _serverUrlBox.Text.Trim();
    public string SyncRoot => _syncRootBox.Text.Trim();
    public string Token => _tokenBox.Text.Trim();

    public SetupForm(string defaultUrl, string defaultSyncRoot, string defaultToken)
    {
        // ====== 窗口属性 ======
        Text = "CloudPan — 连接服务端";
        Size = new Size(540, 470);
        MinimumSize = new Size(480, 430);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = CloudPanColors.BackgroundWhite;
        Font = new Font(CloudPanFonts.FontFamily, 9F);
        Padding = new Padding(0);

        // ====== 职责外提协作类（只存引用，控件在下方初始化后由协作类惰性访问） ======
        _validation = new SetupWizardValidation(this);
        _discovery = new SetupWizardDiscovery(this);
        _layout = new SetupWizardLayout(this, _validation);

        // ====== 输入控件初始化 ======
        _serverUrlBox = SetupWizardLayout.CreateTextBox(defaultUrl, "http://192.168.1.100:8443");
        _syncRootBox = SetupWizardLayout.CreateTextBox(defaultSyncRoot, @"C:\Users\用户名\CloudPan");
        _tokenBox = SetupWizardLayout.CreateTextBox(defaultToken, "粘贴 64 字符家庭 Token");

        // ====== 搜索状态图标（地址框右侧） ======
        _urlStatusIcon = new Label
        {
            Text = "○",
            ForeColor = CloudPanColors.TextMuted,
            TextAlign = ContentAlignment.MiddleCenter,
            AutoSize = false,
            Width = 22,
            Dock = DockStyle.Fill,
            Font = new Font("Segoe UI", 14F),
        };

        // ====== 按钮初始化 ======
        _searchButton = SetupWizardLayout.CreateFlatButton("搜索局域网", 100);
        _searchButton.Click += _discovery.SearchButton_Click;

        _browseButton = SetupWizardLayout.CreateFlatButton("浏览...", 76);
        _browseButton.Click += _discovery.OnBrowseClick;

        _tokenToggleBtn = SetupWizardLayout.CreateFlatButton("显示", 62);
        _tokenToggleBtn.Click += ToggleTokenMask;
        _tokenBox.PasswordChar = '*'; // Token 默认遮蔽（与 SettingsForm 行为一致）

        // ====== 搜索动画定时器（默认停止；必须在 _searchButton 之后创建） ======
        _searchAnimTimer = new System.Windows.Forms.Timer { Interval = 120 };
        _searchAnimTimer.Tick += _discovery.SearchAnimTimer_Tick;

        // ====== 错误/提示标签 ======
        _urlErrorLabel = SetupWizardLayout.CreateFieldMessageLabel();
        _folderErrorLabel = SetupWizardLayout.CreateFieldMessageLabel();
        _tokenErrorLabel = SetupWizardLayout.CreateFieldMessageLabel();

        // ====== Token 下方提示（占位符替代方案） ======
        _tokenHintLabel = new Label
        {
            Text = "在台式机上右键托盘图标 → 复制 Token，然后粘贴到上方",
            ForeColor = CloudPanColors.TextMuted,
            Font = new Font(CloudPanFonts.FontFamily, CloudPanFonts.SizeCaption),
            Dock = DockStyle.Top,
            AutoSize = true,
            Margin = new Padding(0, 2, 0, 0),
        };

        // ====== 状态标签 + 进度条 ======
        _statusLabel = new Label
        {
            Text = "点击「搜索局域网」发现服务端，或手动输入地址",
            AutoSize = true,
            ForeColor = CloudPanColors.TextMuted,
            TextAlign = ContentAlignment.MiddleLeft,
        };

        _progressBar = new ProgressBar
        {
            Style = ProgressBarStyle.Marquee,
            Width = 160,
            Height = 16,
            MarqueeAnimationSpeed = 30,
            Visible = false,
        };

        // ====== 实时校验（失去焦点时立即验证） ======
        _serverUrlBox.Leave += _validation.ServerUrlBox_Leave;
        _syncRootBox.Leave += _validation.SyncRootBox_Leave;
        _tokenBox.Leave += _validation.TokenBox_Leave;

        // 手工编辑地址时重置搜索状态（搜索过程中不重置，搜索成功后用户手动改写时重置）
        _serverUrlBox.TextChanged += _discovery.ServerUrlBox_TextChanged;

        // Token 箱自动清理首尾空白（无长度限制，防止粘贴带空格时显示异常）
        _tokenBox.TextChanged += _validation.TokenBox_TextChanged;

        // ====== 主布局：Dock/Fill + Panel 嵌套 ======
        Panel outerPanel = new Panel { Dock = DockStyle.Fill, BackColor = CloudPanColors.BackgroundWhite };

        // 添加顺序：自底向上
        // 1) 按钮行（Dock.Bottom → 始终在最底）
        var btnRow = _layout.BuildButtonRow();
        if (btnRow.Tag is Button cancelBtn)
        {
            CancelButton = cancelBtn;
        }

        outerPanel.Controls.Add(btnRow);

        // 2) 内容区（Dock.Fill → 撑满中间）
        Panel contentPanel = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(FieldMargin, 4, FieldMargin, 0),
            BackColor = CloudPanColors.BackgroundWhite,
        };
        _layout.BuildContentStack(contentPanel);
        outerPanel.Controls.Add(contentPanel);

        // 3) 分隔线（Dock.Top → header 下方）
        outerPanel.Controls.Add(new Panel
        {
            Dock = DockStyle.Top,
            Height = 1,
            BackColor = CloudPanColors.BorderLight,
        });

        // 4) 头部面板（Dock.Top → 窗口顶部）
        Panel headerPanel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 76,
            BackColor = CloudPanColors.BackgroundWhite,
        };
        headerPanel.Paint += SetupWizardLayout.OnHeaderPaint;
        outerPanel.Controls.Add(headerPanel);

        Controls.Add(outerPanel);

        // ====== 键盘快捷键 ======
        AcceptButton = _okButton;

        // T-032 深色模式：接入主题跟随（当前主题归一化 + 系统切换时刷新）
        ThemeWatcher.Watch(this);
    }

    // ════════════════════════════════════════════════════════════════
    //  共享校验入口（SettingsForm 保存前复用；T-075 下沉为共享静态方法）
    // ════════════════════════════════════════════════════════════════

    /// <summary>检查同步文件夹是否安全可用（复用入口）。返回错误文案，null 表示安全。</summary>
    /// <remarks>拒绝磁盘根目录、系统目录、网络盘、可移动磁盘与 .cloudpan 元数据目录；SetupForm/SettingsForm 保存前复用。</remarks>
    internal static string? ValidateFolderSafety(string folder)
    {
        try
        {
            string normalized = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar);
            if (Path.GetPathRoot(normalized) == normalized)
            {
                return "不能选择磁盘根目录，请选择具体文件夹";
            }

            // 禁止系统目录
            string sysRoot = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
            string progFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string progFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            if (normalized.StartsWith(sysRoot, StringComparison.OrdinalIgnoreCase)
                || normalized.StartsWith(progFiles, StringComparison.OrdinalIgnoreCase)
                || normalized.StartsWith(progFilesX86, StringComparison.OrdinalIgnoreCase))
            {
                return "不能选择系统目录，请选择用户文件夹";
            }

            // 禁止可移动磁盘和网络驱动器
            DriveInfo drive = new DriveInfo(Path.GetPathRoot(normalized)!);
            if (drive.DriveType == DriveType.Network)
            {
                return "不支持网络驱动器，请选择本地文件夹";
            }
            if (drive.DriveType == DriveType.Removable)
            {
                return "不支持移动磁盘，请选择内置硬盘上的文件夹";
            }

            // 禁止把 .cloudpan 元数据目录（或其子目录）作为同步根
            foreach (string segment in normalized.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                if (string.Equals(segment, ".cloudpan", StringComparison.OrdinalIgnoreCase))
                {
                    return "不能将 .cloudpan 元数据目录作为同步文件夹";
                }
            }

            return null; // 安全
        }
        catch (Exception ex)
        {
            return $"路径无效: {ex.Message}";
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  表单控制（取消 / Token 显示切换）
    // ════════════════════════════════════════════════════════════════

    internal void CancelBtn_Click(object? sender, EventArgs e)
    {
        DialogResult = DialogResult.Cancel;
        Close();
    }

    private void ToggleTokenMask(object? sender, EventArgs e)
    {
        _tokenMasked = !_tokenMasked;
        _tokenBox.PasswordChar = _tokenMasked ? '*' : '\0';
        _tokenToggleBtn.Text = _tokenMasked ? "显示" : "隐藏";
        _tokenBox.Select(_tokenBox.TextLength, 0);
    }
}
