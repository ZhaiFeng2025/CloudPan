using System.Drawing.Drawing2D;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using CloudPan.Contract;
using CloudPan.Infrastructure.Design;

namespace CloudPan.Client.UI;

/// <summary>
/// 配置窗口——支持局域网自动发现服务端，输入校验与视觉反馈。
/// </summary>
public partial class SetupForm : Form
{
    private static readonly TimeSpan SearchTimeout = TimeSpan.FromSeconds(5);
    private const int FieldMargin = CloudPanSpacing.MarginStandard;

    // 输入控件
    private readonly TextBox _serverUrlBox;
    private readonly TextBox _syncRootBox;
    private readonly TextBox _tokenBox;

    // 按钮
    private readonly Button _searchButton;
    private readonly Button _browseButton;
    private readonly Button _tokenToggleBtn;
    private Button _okButton = null!;

    // 状态指示
    private readonly Label _statusLabel;
    private readonly Label _urlStatusIcon;
    private readonly ProgressBar _progressBar;

    // 字段级提示（深灰 = 信息性提示，红色 = 阻止提交的错误）
    private readonly Label _urlErrorLabel;
    private readonly Label _folderErrorLabel;
    private readonly Label _tokenErrorLabel;

    // Token 下方提示（替代占位符覆盖层）
    private readonly Label _tokenHintLabel;

    // 搜索按钮旋转动画 + 搜索状态标记
    private readonly System.Windows.Forms.Timer _searchAnimTimer;
    private int _searchAnimFrame;
    private bool _isSearching;
    private bool _searchFound;  // 搜索成功后设置，防止 TextChanged 重置状态图标
    private static readonly string[] SearchSpinner = ["|", "/", "-", "\\"];

    private bool _tokenMasked = true;

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

        // ====== 输入控件初始化 ======
        _serverUrlBox = CreateTextBox(defaultUrl, "http://192.168.1.100:8443");
        _syncRootBox = CreateTextBox(defaultSyncRoot, @"C:\Users\用户名\CloudPan");
        _tokenBox = CreateTextBox(defaultToken, "粘贴 64 字符家庭 Token");

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
        _searchButton = CreateFlatButton("搜索局域网", 100);
        _searchButton.Click += SearchButton_Click;

        _browseButton = CreateFlatButton("浏览...", 76);
        _browseButton.Click += OnBrowseClick;

        _tokenToggleBtn = CreateFlatButton("显示", 62);
        _tokenToggleBtn.Click += ToggleTokenMask;
        _tokenBox.PasswordChar = '*'; // Token 默认遮蔽（与 SettingsForm 行为一致）

        // ====== 搜索动画定时器（默认停止；必须在 _searchButton 之后创建） ======
        _searchAnimTimer = new System.Windows.Forms.Timer { Interval = 120 };
        _searchAnimTimer.Tick += SearchAnimTimer_Tick;

        // ====== 错误/提示标签 ======
        _urlErrorLabel = CreateFieldMessageLabel();
        _folderErrorLabel = CreateFieldMessageLabel();
        _tokenErrorLabel = CreateFieldMessageLabel();

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
        _serverUrlBox.Leave += ServerUrlBox_Leave;
        _syncRootBox.Leave += SyncRootBox_Leave;
        _tokenBox.Leave += TokenBox_Leave;

        // 手工编辑地址时重置搜索状态（搜索过程中不重置，搜索成功后用户手动改写时重置）
        _serverUrlBox.TextChanged += ServerUrlBox_TextChanged;

        // Token 箱自动清理首尾空白（无长度限制，防止粘贴带空格时显示异常）
        _tokenBox.TextChanged += TokenBox_TextChanged;

        // ====== 主布局：Dock/Fill + Panel 嵌套 ======
        Panel outerPanel = new Panel { Dock = DockStyle.Fill, BackColor = CloudPanColors.BackgroundWhite };

        // 添加顺序：自底向上
        // 1) 按钮行（Dock.Bottom → 始终在最底）
        var btnRow = BuildButtonRow();
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
        BuildContentStack(contentPanel);
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
        headerPanel.Paint += OnHeaderPaint;
        outerPanel.Controls.Add(headerPanel);

        Controls.Add(outerPanel);

        // ====== 键盘快捷键 ======
        AcceptButton = _okButton;
    }

    // ════════════════════════════════════════════════════════════════
    //  表单控制（取消 / Token 显示切换）
    // ════════════════════════════════════════════════════════════════

    private void CancelBtn_Click(object? sender, EventArgs e)
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
