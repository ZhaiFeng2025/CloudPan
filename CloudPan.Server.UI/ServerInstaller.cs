using System.Security.Principal;
using CloudPan.Infrastructure.Design;

namespace CloudPan.Server.UI;

/// <summary>
/// 服务端安装向导——带界面的安装程序。
/// </summary>
public partial class ServerInstaller : Form
{
    private readonly Label _titleLabel = null!;
    private readonly Label _statusLabel = null!;
    private readonly ProgressBar _progressBar = null!;
    private readonly Button _installBtn = null!;
    private readonly Button _closeBtn = null!;
    private readonly TextBox _tokenBox = null!;
    private readonly Panel _tokenPanel = null!;
    private readonly Panel _tokenBorder = null!;
    private readonly Panel _stepPanel = null!;
    private readonly Panel _tokenArea = null!; // 用于 InstallAsync 中控制可见性
    private readonly Panel _syncDirPanel = null!;
    private readonly TextBox _syncDirBox = null!;
    private Button _copyBtn = null!; // 复制按钮（提升为字段以便具名事件处理器访问）
    private int _currentStep = -1; // -1:未开始, 0-4:步骤中, 5:全部完成
    private int _flashCount; // 闪烁动画计数
    private Color _flashColor; // 闪烁动画绿色
    private Color _flashOriginalColor; // 闪烁动画起始边框色

    public ServerInstaller()
    {
        // 管理员权限预检
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        WindowsPrincipal principal = new WindowsPrincipal(identity);
        if (!principal.IsInRole(WindowsBuiltInRole.Administrator))
        {
            MessageBox.Show("安装 Windows Service 需要管理员权限。\n\n" +
                "程序将以独立模式运行（无需管理员）。\n" +
                "如需安装服务，请右键以管理员身份运行此程序。",
                "权限提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            // 不杀进程，返回 Abort 让调用方以独立模式继续
            Load += OnNonAdminLoad;
            return;
        }

        Text = "CloudPan Server — 安装向导";
        Size = new Size(520, 500);
        MinimumSize = new Size(500, 460);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false;
        BackColor = CloudPanColors.BackgroundWhite;
        Font = new Font(CloudPanFonts.FontFamily, 9F);

        // ========== 标题栏 ==========
        Panel headerPanel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 72,
            BackColor = CloudPanColors.PrimaryBlue,
            Padding = new Padding(24, 14, 24, 0)
        };
        _titleLabel = new Label
        {
            Text = "CloudPan 服务端安装",
            ForeColor = CloudPanColors.TextOnPrimary,
            Font = new Font(CloudPanFonts.FontFamily, 16, FontStyle.Bold),
            AutoSize = true
        };
        Label subtitle = new Label
        {
            Text = "将在此计算机上安装文件同步服务",
            ForeColor = CloudPanColors.TextOnPrimary,
            Font = new Font(CloudPanFonts.FontFamily, 9),
            AutoSize = true,
            Location = new Point(24, 48)
        };
        headerPanel.Controls.Add(_titleLabel);
        headerPanel.Controls.Add(subtitle);

        // ========== 步骤指示器 ==========
        _stepPanel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 48,
            BackColor = CloudPanColors.BackgroundWhite
        };
        _stepPanel.Paint += StepPanel_Paint;

        // ========== 主体区域 ==========
        Panel bodyPanel = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(0)
        };

        // 状态文字
        _statusLabel = new Label
        {
            Dock = DockStyle.Top,
            Height = 36,
            Padding = new Padding(24, 10, 24, 0),
            AutoSize = false,
            ForeColor = CloudPanColors.TextSecondary,
            Font = new Font(CloudPanFonts.FontFamily, CloudPanFonts.SizeBody),
            Text = "安装将为 Windows Service，开机自动启动。\n请选择同步文件存储目录。"
        };

        // ========== 同步目录选择（M-05） ==========
        _syncDirPanel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 40,
            Padding = new Padding(24, 6, 24, 0)
        };

        Label syncDirLabel = new Label
        {
            Text = "同步目录：",
            AutoSize = true,
            Location = new Point(0, 5),
            ForeColor = CloudPanColors.TextPrimary,
            Font = new Font(CloudPanFonts.FontFamily, CloudPanFonts.SizeBody)
        };

        _syncDirBox = new TextBox
        {
            Location = new Point(70, 3),
            Width = 280,
            Height = 22,
            Text = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "CloudPan"),
            Font = new Font(CloudPanFonts.FontFamily, CloudPanFonts.SizeBody)
        };

        Button browseBtn = new Button
        {
            Text = "浏览...",
            Location = new Point(356, 2),
            Size = new Size(70, 24),
            FlatStyle = FlatStyle.Flat,
            BackColor = CloudPanColors.PrimaryBlue,
            ForeColor = CloudPanColors.TextOnPrimary,
            Cursor = Cursors.Hand
        };
        browseBtn.FlatAppearance.BorderSize = 0;
        browseBtn.Click += BrowseBtn_Click;

        _syncDirPanel.Controls.Add(browseBtn);
        _syncDirPanel.Controls.Add(_syncDirBox);
        _syncDirPanel.Controls.Add(syncDirLabel);

        // 进度条（带水平边距的容器）
        Panel progressWrapper = new Panel
        {
            Dock = DockStyle.Top,
            Height = 20,
            Padding = new Padding(24, 4, 24, 0)
        };
        _progressBar = new ProgressBar
        {
            Dock = DockStyle.Fill,
            Height = 6,
            Style = ProgressBarStyle.Continuous,
            Maximum = 5,
            Visible = false
        };
        progressWrapper.Controls.Add(_progressBar);

        // ---- Token 区域（带绿色边框闪烁动画） ----
        _tokenArea = new Panel
        {
            Dock = DockStyle.Top,
            Height = 120,
            Visible = false,
            Padding = new Padding(24, 6, 24, 0)
        };

        // 外层作为 "边框"（1px padding → BackColor 即边框色）
        _tokenBorder = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = CloudPanColors.BorderLight,
            Padding = new Padding(1)
        };

        // 内层白色内容面板
        _tokenPanel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = CloudPanColors.BackgroundWhite
        };

        Label tokenLabel = new Label
        {
            Text = "家庭共享 Token（请妥善保存，配置客户端需要）：",
            Location = new Point(10, 6),
            AutoSize = true,
            Font = new Font(CloudPanFonts.FontFamily, CloudPanFonts.SizeCaption, FontStyle.Bold),
            ForeColor = CloudPanColors.TextPrimary
        };

        _tokenBox = new TextBox
        {
            Location = new Point(10, 28),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            ReadOnly = true,
            Font = new Font("Consolas", 10),
            BorderStyle = BorderStyle.None,
            BackColor = CloudPanColors.BackgroundWhite,
            ForeColor = CloudPanColors.TextPrimary
        };

        _copyBtn = new Button
        {
            Text = "复制",
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            FlatStyle = FlatStyle.Flat,
            BackColor = CloudPanColors.PrimaryBlue,
            ForeColor = CloudPanColors.TextOnPrimary,
            Font = new Font(CloudPanFonts.FontFamily, CloudPanFonts.SizeCaption, FontStyle.Bold),
            Cursor = Cursors.Hand,
            Size = new Size(70, 26)
        };
        _copyBtn.FlatAppearance.BorderSize = 0;
        _copyBtn.Click += CopyBtn_Click;

        // 定位复制按钮和输入框宽度
        _tokenPanel.Layout += TokenPanel_Layout;

        _tokenPanel.Controls.Add(tokenLabel);
        _tokenPanel.Controls.Add(_tokenBox);
        _tokenPanel.Controls.Add(_copyBtn);
        _tokenBorder.Controls.Add(_tokenPanel);
        _tokenArea.Controls.Add(_tokenBorder);

        bodyPanel.Controls.Add(_tokenArea);
        bodyPanel.Controls.Add(progressWrapper);
        bodyPanel.Controls.Add(_syncDirPanel);
        bodyPanel.Controls.Add(_statusLabel);

        // ========== 按钮区（底部） ==========
        Panel btnPanel = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 50,
        };

        _installBtn = new Button
        {
            Text = "开始安装",
            Size = new Size(CloudPanSpacing.ButtonWidth, CloudPanSpacing.InputHeight),
            Location = new Point(24, 6),
            BackColor = CloudPanColors.PrimaryBlue,
            ForeColor = CloudPanColors.TextOnPrimary,
            FlatStyle = FlatStyle.Flat,
            Font = new Font(CloudPanFonts.FontFamily, 10, FontStyle.Bold),
            Cursor = Cursors.Hand
        };
        _installBtn.FlatAppearance.BorderSize = 0;
        _installBtn.Click += InstallBtn_Click;

        _closeBtn = new Button
        {
            Text = "完成",
            Size = new Size(120, 38),
            Visible = false,
            FlatStyle = FlatStyle.Flat,
            BackColor = CloudPanColors.BackgroundWhite,
            ForeColor = CloudPanColors.TextSecondary,
            Font = new Font(CloudPanFonts.FontFamily, 10, FontStyle.Bold),
            Cursor = Cursors.Hand
        };
        _closeBtn.FlatAppearance.BorderColor = CloudPanColors.BorderMid;
        _closeBtn.FlatAppearance.MouseOverBackColor = CloudPanColors.ButtonHoverBg;
        _closeBtn.FlatAppearance.MouseDownBackColor = CloudPanColors.ButtonPressBg;
        _closeBtn.Click += CloseBtn_Click;

        // 定位关闭按钮（右侧，24px 边距）
        btnPanel.Layout += BtnPanel_Layout;
        _closeBtn.Location = new Point(0, 6); // 初始位置，Layout 事件会修正

        btnPanel.Controls.Add(_closeBtn);
        btnPanel.Controls.Add(_installBtn);

        // 按添加顺序：底部 → 顶部（Dock 后添加的靠近对应边缘）
        Controls.Add(btnPanel);
        Controls.Add(bodyPanel);
        Controls.Add(_stepPanel);
        Controls.Add(headerPanel);

        // T-032 深色模式：接入主题跟随（当前主题归一化 + 系统切换时刷新）
        ThemeWatcher.Watch(this);
    }

    // =================================================================
    //  具名事件处理器（CP301：避免匿名 lambda 订阅无法退订）
    // =================================================================
    private void OnNonAdminLoad(object? sender, EventArgs e)
    {
        DialogResult = DialogResult.Abort;
        Close();
    }

    private void BrowseBtn_Click(object? sender, EventArgs e)
    {
        using FolderBrowserDialog dlg = new FolderBrowserDialog();
        dlg.Description = "选择同步文件存储目录";
        dlg.SelectedPath = _syncDirBox.Text;
        if (dlg.ShowDialog() == DialogResult.OK)
        {
            _syncDirBox.Text = dlg.SelectedPath;
        }
    }

    private void CopyBtn_Click(object? sender, EventArgs e)
    {
        string rawToken = _tokenBox.Text.Replace("-", "");
        try { Clipboard.SetText(rawToken); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"复制 Token 失败: {ex.Message}"); }
        _copyBtn.Text = "已复制!";
        _copyBtn.BackColor = CloudPanColors.SuccessGreen;
        Task.Run(async () =>
        {
            try { await Task.Delay(1500); } catch { }
            try
            {
                if (!_copyBtn.IsDisposed)
                {
                    _copyBtn.Invoke(() =>
                    {
                        if (!_copyBtn.IsDisposed)
                        {
                            _copyBtn.Text = "复制";
                            _copyBtn.BackColor = CloudPanColors.PrimaryBlue;
                        }
                    });
                }
            }
            catch (ObjectDisposedException) { }
        });
    }

    private void TokenPanel_Layout(object? sender, LayoutEventArgs e)
    {
        _copyBtn.Location = new Point(_tokenPanel.Width - 80, 26);
        _tokenBox.Width = _tokenPanel.Width - 100;
    }

    private async void InstallBtn_Click(object? sender, EventArgs e) => await InstallAsync();

    private void CloseBtn_Click(object? sender, EventArgs e) => Close();

    private void BtnPanel_Layout(object? sender, LayoutEventArgs e)
    {
        if (sender is Panel panel)
        {
            _closeBtn.Location = new Point(panel.Width - 24 - 120, 6);
        }
    }
}
