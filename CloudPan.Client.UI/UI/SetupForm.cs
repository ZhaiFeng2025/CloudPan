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
public class SetupForm : Form
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
    //  布局构建
    // ════════════════════════════════════════════════════════════════

    /// <summary>构建内容区的垂直堆叠控件。</summary>
    /// <remarks>
    /// 使用 Dock.Top 堆叠，添加顺序即为视觉从上到下的顺序。
    /// （Dock 按逆 Z 序处理，最后添加的控件 Z 序最高、最先被 Dock → 顶部。）
    /// </remarks>
    private void BuildContentStack(Panel parent)
    {
        // 弹性填充（确保所有字段靠上，额外空间在底部留白）
        parent.Controls.Add(new Panel { Dock = DockStyle.Fill });

        // ── 状态行 ──
        FlowLayoutPanel statusRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Height = 28,
        };
        statusRow.Controls.Add(_progressBar);
        statusRow.Controls.Add(_statusLabel);
        parent.Controls.Add(statusRow);

        // Spacer
        parent.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 6 });

        // ── Token 提示（输入框下方常驻说明） ──
        parent.Controls.Add(_tokenHintLabel);

        // ── Token 错误标签（在输入行下方、提示上方） ──
        parent.Controls.Add(_tokenErrorLabel);

        // ── Token 输入行 ──
        parent.Controls.Add(BuildTokenInputRow());

        // ── Token 标签行 ──
        FlowLayoutPanel tokenLabelRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Height = 24,
        };
        tokenLabelRow.Controls.Add(new Label
        {
            Text = "家庭 Token",
            AutoSize = true,
            TextAlign = ContentAlignment.BottomLeft,
            ForeColor = CloudPanColors.TextSecondary,
            Font = new Font(CloudPanFonts.FontFamily, 9F, FontStyle.Bold),
        });
        parent.Controls.Add(tokenLabelRow);

        // Spacer
        parent.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 6 });

        // ── 文件夹错误标签 ──
        parent.Controls.Add(_folderErrorLabel);

        // ── 文件夹输入行 ──
        parent.Controls.Add(BuildInputRow(_syncRootBox, _browseButton));

        // ── 文件夹标签 ──
        parent.Controls.Add(new Label
        {
            Text = "同步文件夹",
            Dock = DockStyle.Top,
            AutoSize = true,
            TextAlign = ContentAlignment.BottomLeft,
            Padding = new Padding(0, 4, 0, 2),
            ForeColor = CloudPanColors.TextSecondary,
            Font = new Font(CloudPanFonts.FontFamily, 9F, FontStyle.Bold),
        });

        // Spacer
        parent.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 6 });

        // ── URL 错误标签 ──
        parent.Controls.Add(_urlErrorLabel);

        // ── URL 输入行（TextBox + 状态图标 + 搜索按钮） ──
        parent.Controls.Add(BuildUrlInputRow());

        // ── URL 标签 ──
        parent.Controls.Add(new Label
        {
            Text = "服务端地址",
            Dock = DockStyle.Top,
            AutoSize = true,
            TextAlign = ContentAlignment.BottomLeft,
            Padding = new Padding(0, 4, 0, 2),
            ForeColor = CloudPanColors.TextSecondary,
            Font = new Font(CloudPanFonts.FontFamily, 9F, FontStyle.Bold),
        });
    }

    /// <summary>URL 输入行：TextBox + 状态图标 + 搜索按钮。</summary>
    private Panel BuildUrlInputRow()
    {
        TableLayoutPanel row = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 34,
            ColumnCount = 3,
            RowCount = 1,
            Margin = new Padding(0),
        };
        row.ColumnStyles.Clear();
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 22F));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100F));

        _serverUrlBox.Dock = DockStyle.Fill;
        _searchButton.Dock = DockStyle.Fill;
        _searchButton.Margin = new Padding(6, 0, 0, 0);
        _urlStatusIcon.Margin = new Padding(4, 0, 0, 0);

        row.Controls.Add(_serverUrlBox, 0, 0);
        row.Controls.Add(_urlStatusIcon, 1, 0);
        row.Controls.Add(_searchButton, 2, 0);

        return row;
    }

    /// <summary>Token 输入行：TextBox + 显示/隐藏按钮。</summary>
    private Panel BuildTokenInputRow()
    {
        TableLayoutPanel row = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 34,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0),
        };
        row.ColumnStyles.Clear();
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 62F));

        _tokenBox.Dock = DockStyle.Fill;
        _tokenToggleBtn.Dock = DockStyle.Fill;
        _tokenToggleBtn.Margin = new Padding(6, 0, 0, 0);

        row.Controls.Add(_tokenBox, 0, 0);
        row.Controls.Add(_tokenToggleBtn, 1, 0);

        return row;
    }

    /// <summary>通用输入行：TextBox + Button。</summary>
    private static Panel BuildInputRow(TextBox textBox, Button button)
    {
        TableLayoutPanel row = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 34,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0),
        };
        row.ColumnStyles.Clear();
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, button.Width + 6));

        textBox.Dock = DockStyle.Fill;
        button.Dock = DockStyle.Fill;
        button.Margin = new Padding(6, 0, 0, 0);

        row.Controls.Add(textBox, 0, 0);
        row.Controls.Add(button, 1, 0);

        return row;
    }

    /// <summary>底部操作按钮行。内部创建 _okButton 和取消按钮。</summary>
    private Panel BuildButtonRow()
    {
        FlowLayoutPanel btnRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Height = 56,
            Padding = new Padding(FieldMargin, 0, FieldMargin, 12),
            BackColor = CloudPanColors.BackgroundWhite,
        };

        _okButton = new Button
        {
            Text = "连接服务器",
            Width = CloudPanSpacing.ButtonWidth,
            Height = CloudPanSpacing.InputHeight,
            FlatStyle = FlatStyle.Flat,
            BackColor = CloudPanColors.PrimaryBlue,
            ForeColor = CloudPanColors.TextOnPrimary,
            Font = new Font(CloudPanFonts.FontFamily, 10F, FontStyle.Bold),
            Cursor = Cursors.Hand,
            UseVisualStyleBackColor = false,
        };
        _okButton.FlatAppearance.BorderSize = 0;
        _okButton.FlatAppearance.MouseOverBackColor = CloudPanColors.PrimaryBlueHover;
        _okButton.FlatAppearance.MouseDownBackColor = CloudPanColors.PrimaryBluePress;
        _okButton.Click += OnOkClick;

        Button cancelBtn = new Button
        {
            Text = "退出",
            Width = 72,
            Height = 34,
            FlatStyle = FlatStyle.Flat,
            BackColor = CloudPanColors.BackgroundWhite,
            ForeColor = CloudPanColors.TextSecondary,
            Font = new Font(CloudPanFonts.FontFamily, 9F),
            Cursor = Cursors.Hand,
            Margin = new Padding(8, 0, 0, 0),
            UseVisualStyleBackColor = false,
        };
        cancelBtn.FlatAppearance.BorderColor = CloudPanColors.BorderLight;
        cancelBtn.FlatAppearance.MouseOverBackColor = CloudPanColors.ButtonHoverBg;
        cancelBtn.FlatAppearance.MouseDownBackColor = CloudPanColors.ButtonPressBg;
        cancelBtn.Click += CancelBtn_Click;

        btnRow.Controls.Add(_okButton);
        btnRow.Controls.Add(cancelBtn);

        // CancelButton 在构造函数中设置
        btnRow.Tag = cancelBtn;
        return btnRow;
    }

    // ════════════════════════════════════════════════════════════════
    //  辅助构建
    // ════════════════════════════════════════════════════════════════

    private static TextBox CreateTextBox(string text, string placeholder)
    {
        return new TextBox
        {
            Text = text,
            PlaceholderText = placeholder,
            Font = new Font("Consolas", 10F),
            ForeColor = CloudPanColors.TextPrimary,
            BackColor = CloudPanColors.BackgroundWhite,
            BorderStyle = BorderStyle.FixedSingle,
        };
    }

    private static Button CreateFlatButton(string text, int width)
    {
        Button btn = new Button
        {
            Text = text,
            Width = width,
            FlatStyle = FlatStyle.Flat,
            BackColor = CloudPanColors.BackgroundWhite,
            ForeColor = CloudPanColors.TextSecondary,
            Font = new Font(CloudPanFonts.FontFamily, 9F),
            Cursor = Cursors.Hand,
            TextAlign = ContentAlignment.MiddleCenter,
            UseVisualStyleBackColor = false,
        };
        btn.FlatAppearance.BorderColor = CloudPanColors.BorderLight;
        btn.FlatAppearance.MouseOverBackColor = CloudPanColors.ButtonHoverBg;
        btn.FlatAppearance.MouseDownBackColor = CloudPanColors.ButtonPressBg;
        return btn;
    }

    private static Label CreateFieldMessageLabel()
    {
        return new Label
        {
            Text = "",
            Dock = DockStyle.Top,
            AutoSize = true,
            Margin = new Padding(0, 1, 0, 4),
            ForeColor = CloudPanColors.TextDarkGray,
            Font = new Font(CloudPanFonts.FontFamily, CloudPanFonts.SizeCaption),
            Visible = false,
        };
    }

    // ════════════════════════════════════════════════════════════════
    //  Header 绘制（复用 CloudPanIcon，保证与托盘图标一致）
    // ════════════════════════════════════════════════════════════════

    private static void OnHeaderPaint(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        const int iconSize = 36;
        const int margin = 28;
        int iconY = (76 - iconSize) / 2;

        // 使用 CloudPanIcon 绘制蓝色圆形云朵图标（与系统托盘图标一致）
        using (var fullIcon = CloudPanIcon.Create())
        using (Icon icon = new Icon(fullIcon, iconSize, iconSize))
        {
            g.DrawIcon(icon, margin, iconY);
        }

        // 标题
        int textX = margin + iconSize + 14;
        using (Font tf = new Font(CloudPanFonts.FontFamily, CloudPanFonts.SizeSubtitle, FontStyle.Bold))
        using (SolidBrush tb = new SolidBrush(CloudPanColors.TextPrimary))
        {
            g.DrawString("CloudPan 文件同步", tf, tb, textX, iconY + 1);
        }

        // 副标题
        using (Font sf = new Font(CloudPanFonts.FontFamily, 9F))
        using (SolidBrush sb = new SolidBrush(CloudPanColors.TextMuted))
        {
            g.DrawString("连接家庭文件同步服务端", sf, sb, textX, iconY + 27);
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  Token 显示/隐藏
    // ════════════════════════════════════════════════════════════════

    // ===== 具名事件处理器（CP301：避免匿名 lambda 订阅无法退订） =====

    private async void SearchButton_Click(object? sender, EventArgs e) => await SearchLanAsync();

    private void SearchAnimTimer_Tick(object? sender, EventArgs e)
    {
        _searchAnimFrame = (_searchAnimFrame + 1) % SearchSpinner.Length;
        _searchButton.Text = "搜索中 " + SearchSpinner[_searchAnimFrame];
    }

    private void ServerUrlBox_Leave(object? sender, EventArgs e) => ValidateServerUrlField();

    private void SyncRootBox_Leave(object? sender, EventArgs e) => ValidateSyncRootField();

    private void TokenBox_Leave(object? sender, EventArgs e) => ValidateTokenField();

    private void ServerUrlBox_TextChanged(object? sender, EventArgs e)
    {
        if (!_isSearching)
        {
            if (_searchFound)
            {
                // 搜索成功后用户手动改写 → 清除搜索状态，让图标重新变为空心
                _searchFound = false;
            }
            _urlStatusIcon.Text = "○";
            _urlStatusIcon.ForeColor = CloudPanColors.TextMuted;
        }
    }

    private void TokenBox_TextChanged(object? sender, EventArgs e)
    {
        string trimmed = _tokenBox.Text.Trim();
        if (trimmed != _tokenBox.Text)
        {
            _tokenBox.Text = trimmed;
        }
    }

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

    // ════════════════════════════════════════════════════════════════
    //  文件夹安全验证（保持原逻辑不变）
    // ════════════════════════════════════════════════════════════════

    /// <summary>检查文件夹是否安全可用——禁止系统目录、根目录、移动设备。</summary>
    /// <param name="useHintColors">实时校验时用深灰提示色，提交时用红色。</param>
    private bool ValidateFolderSafety(string folder, bool useHintColors = false)
    {
        try
        {
            string normalized = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar);
            bool isRoot = Path.GetPathRoot(normalized) == normalized;
            if (isRoot)
            {
                ShowFieldMessage(_folderErrorLabel, "不能选择磁盘根目录，请选择具体文件夹",
                    useHintColors ? MessageSeverity.Hint : MessageSeverity.Error);
                return false;
            }

            // 禁止系统目录
            string sysRoot = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
            string progFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string progFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            if (normalized.StartsWith(sysRoot, StringComparison.OrdinalIgnoreCase)
                || normalized.StartsWith(progFiles, StringComparison.OrdinalIgnoreCase)
                || normalized.StartsWith(progFilesX86, StringComparison.OrdinalIgnoreCase))
            {
                ShowFieldMessage(_folderErrorLabel, "不能选择系统目录，请选择用户文件夹",
                    useHintColors ? MessageSeverity.Hint : MessageSeverity.Error);
                return false;
            }

            // 禁止可移动磁盘和网络驱动器
            DriveInfo drive = new DriveInfo(Path.GetPathRoot(normalized)!);
            if (drive.DriveType == DriveType.Network)
            {
                ShowFieldMessage(_folderErrorLabel, "不支持网络驱动器，请选择本地文件夹",
                    useHintColors ? MessageSeverity.Hint : MessageSeverity.Error);
                return false;
            }
            if (drive.DriveType == DriveType.Removable)
            {
                ShowFieldMessage(_folderErrorLabel, "不支持移动磁盘，请选择内置硬盘上的文件夹",
                    useHintColors ? MessageSeverity.Hint : MessageSeverity.Error);
                return false;
            }

            // 检查是否被其他同步服务接管（使用环境变量判断云盘路径）
            var cloudDrivePaths = new[]
            {
                (path: Environment.GetEnvironmentVariable("OneDrive"), name: "OneDrive"),
                (path: Environment.GetEnvironmentVariable("OneDriveConsumer"), name: "OneDrive"),
                (path: Environment.GetEnvironmentVariable("DROPBOX_HOME"), name: "Dropbox"),
                (path: Environment.GetEnvironmentVariable("iCloudDrive"), name: "iCloud"),
            };
            foreach (var (cloudPath, serviceName) in cloudDrivePaths)
            {
                if (!string.IsNullOrEmpty(cloudPath) && normalized.StartsWith(cloudPath, StringComparison.OrdinalIgnoreCase))
                {
                    ShowFieldMessage(_folderErrorLabel,
                        $"此文件夹在 {serviceName} 同步范围内，可能造成同步冲突。确认要使用此文件夹吗？",
                        MessageSeverity.Hint); // 改为提示不阻断，让用户自行确认
                    // 不 return false，仅提示
                }
            }

            // 统计文件夹内容（显示文件数量，帮助用户做决策）
            // 在后台线程执行枚举，前台最多等 2 秒，避免巨量文件阻塞 UI
            try
            {
                var (count, totalSize) = CountFolderContentsSafe(normalized);
                if (count > 0)
                {
                    string sizeStr = totalSize > 1_048_576 ? $"{totalSize / 1_048_576} MB"
                        : totalSize > 1024 ? $"{totalSize / 1024} KB" : $"{totalSize} B";
                    _folderErrorLabel.Text = count >= 10000
                        ? $"此文件夹包含超过 {count} 个文件，首次同步需要较长时间"
                        : count > 100
                        ? $"此文件夹包含 {count} 个文件（约 {sizeStr}），首次同步需要一些时间"
                        : $"此文件夹包含 {count} 个文件（{sizeStr}）";
                    _folderErrorLabel.ForeColor = CloudPanColors.TextDarkGray;
                    _folderErrorLabel.Visible = true;
                }
                else
                {
                    HideFieldMessage(_folderErrorLabel);
                }
            }
            catch { /* 权限不足 —— 不干扰用户 */ }

            return true;
        }
        catch (Exception ex)
        {
            ShowFieldMessage(_folderErrorLabel, $"路径无效: {ex.Message}",
                useHintColors ? MessageSeverity.Hint : MessageSeverity.Error);
            return false;
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  实时校验（失去焦点时显示柔和提示）
    // ════════════════════════════════════════════════════════════════

    private void ValidateServerUrlField()
    {
        string url = _serverUrlBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(url))
        {
            ShowFieldHint(_urlErrorLabel, "请输入服务端地址，如 http://192.168.1.100:8443");
            return;
        }
        if (!url.StartsWith("http://") && !url.StartsWith("https://"))
        {
            ShowFieldHint(_urlErrorLabel, "地址需以 http:// 或 https:// 开头");
            return;
        }
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || string.IsNullOrWhiteSpace(uri.Host)
            || uri.Port < 1 || uri.Port > 65535)
        {
            ShowFieldHint(_urlErrorLabel, "地址格式不正确（请检查 IP/域名和端口号 1-65535）");
            return;
        }
        if (!string.IsNullOrEmpty(uri.AbsolutePath) && uri.AbsolutePath != "/")
        {
            ShowFieldHint(_urlErrorLabel, "地址不应包含路径，只需 http://IP:端口");
            return;
        }
        HideFieldMessage(_urlErrorLabel);
    }

    private void ValidateSyncRootField()
    {
        string folder = _syncRootBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(folder))
        {
            ShowFieldHint(_folderErrorLabel, "请选择或输入同步文件夹路径");
            return;
        }
        if (folder.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            ShowFieldHint(_folderErrorLabel, "路径包含非法字符");
            return;
        }
        // 安全校验 + 统计信息，实时模式用深灰提示
        ValidateFolderSafety(folder, useHintColors: true);
    }

    private void ValidateTokenField()
    {
        string token = _tokenBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(token))
        {
            ShowFieldHint(_tokenErrorLabel, "请输入家庭 Token");
            return;
        }
        if (token.Length != 64 || !IsHexString(token))
        {
            ShowFieldHint(_tokenErrorLabel, "Token 应为 64 个十六进制字符");
            return;
        }
        HideFieldMessage(_tokenErrorLabel);
    }

    // ════════════════════════════════════════════════════════════════
    //  OK 点击 —— 完整校验 + 提交
    // ════════════════════════════════════════════════════════════════

    private void OnOkClick(object? sender, EventArgs e)
    {
        // 搜索进行中禁止提交
        if (_isSearching)
        {
            ShowFieldHint(_statusLabel, "请等待搜索完成后再连接服务器");
            return;
        }

        _okButton.Enabled = false;
        if (!ValidateInputs())
        {
            _okButton.Enabled = true;
            return;
        }
        DialogResult = DialogResult.OK;
        Close();
    }

    private bool ValidateInputs()
    {
        bool valid = true;
        bool focusSet = false;

        // 服务端地址
        string url = ServerUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            ShowFieldError(_urlErrorLabel, "请输入服务端地址");
            valid = false;
            if (!focusSet) { _serverUrlBox.Focus(); focusSet = true; }
        }
        else if (!url.StartsWith("http://") && !url.StartsWith("https://"))
        {
            ShowFieldError(_urlErrorLabel, "请输入完整地址，如 http://192.168.1.100:8443");
            valid = false;
            if (!focusSet) { _serverUrlBox.Focus(); focusSet = true; }
        }
        else if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
                 || string.IsNullOrWhiteSpace(uri.Host)
                 || uri.Port < 1 || uri.Port > 65535)
        {
            ShowFieldError(_urlErrorLabel, "地址格式不正确（请检查 IP/域名和端口号 1-65535）");
            valid = false;
            if (!focusSet) { _serverUrlBox.Focus(); focusSet = true; }
        }
        else
        {
            HideFieldMessage(_urlErrorLabel);
        }

        // 同步文件夹
        string folder = SyncRoot;
        if (string.IsNullOrWhiteSpace(folder))
        {
            ShowFieldError(_folderErrorLabel, "请输入同步文件夹路径");
            valid = false;
            if (!focusSet) { _syncRootBox.Focus(); focusSet = true; }
        }
        else if (folder.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            ShowFieldError(_folderErrorLabel, "路径包含非法字符");
            valid = false;
            if (!focusSet) { _syncRootBox.Focus(); focusSet = true; }
        }
        else if (!ValidateFolderSafety(folder, useHintColors: false))
        {
            valid = false;
            if (!focusSet) { _syncRootBox.Focus(); focusSet = true; }
        }
        else
        {
            HideFieldMessage(_folderErrorLabel);
        }

        // 家庭 Token
        string token = Token;
        if (string.IsNullOrWhiteSpace(token))
        {
            ShowFieldError(_tokenErrorLabel, "请输入家庭 Token");
            valid = false;
            if (!focusSet) { _tokenBox.Focus(); focusSet = true; }
        }
        else if (token.Length != 64 || !IsHexString(token))
        {
            ShowFieldError(_tokenErrorLabel, "Token 格式不正确，请完整粘贴服务端显示的 64 个字符");
            valid = false;
            if (!focusSet) { _tokenBox.Focus(); focusSet = true; }
        }
        else
        {
            HideFieldMessage(_tokenErrorLabel);
        }

        return valid;
    }

    private static bool IsHexString(string s) =>
        !string.IsNullOrEmpty(s) && s.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'));

    /// <summary>
    /// 在后台线程统计文件夹内容，前台最多等待 2 秒。
    /// 超时或失败时返回 (0, 0) 以跳过详细统计显示。
    /// </summary>
    private static (int count, long totalSize) CountFolderContentsSafe(string normalized)
    {
        int count = 0;
        long totalSize = 0;
        Task<(int count, long totalSize)> task = Task.Run(() =>
        {
            foreach (string f in Directory.EnumerateFiles(normalized, "*", SearchOption.AllDirectories))
            {
                count++;
                if (count > 10000)
                {
                    return (count, totalSize);
                }

                if (count % 100 == 0)
                {
                    continue; // 每 100 个文件跳过 size 计算以节省时间
                }

                try { totalSize += new FileInfo(f).Length; } catch { }
            }
            return (count, totalSize);
        });
        if (task.Wait(TimeSpan.FromSeconds(2)))
        {
            return task.Result;
        }
        // 超时：返回 0 表示不显示详细统计
        return (0, 0);
    }

    // ════════════════════════════════════════════════════════════════
    //  局域网搜索（UDP 广播）
    // ════════════════════════════════════════════════════════════════

    /// <summary>搜索局域网内的 CloudPan 服务端（不再在窗口加载时自动触发）。</summary>
    private async Task SearchLanAsync()
    {
        _isSearching = true;
        _searchButton.Enabled = false;
        _searchAnimFrame = 0;
        _searchAnimTimer.Start();
        _progressBar.Visible = true;
        _statusLabel.Text = "正在搜索局域网服务端...";
        _statusLabel.ForeColor = CloudPanColors.TextMuted;
        _urlStatusIcon.Text = "○";
        _urlStatusIcon.ForeColor = CloudPanColors.TextMuted;

        bool found = false;
        string? errorMessage = null;

        try
        {
            using UdpClient udp = new UdpClient();
            udp.EnableBroadcast = true;
            byte[] request = Encoding.UTF8.GetBytes("CLOUDPAN_DISCOVER");
            await udp.SendAsync(request, request.Length, new IPEndPoint(IPAddress.Broadcast, SpecPorts.UdpDiscoveryPort));

            using CancellationTokenSource cts = new CancellationTokenSource(SearchTimeout);
            try
            {
                var result = await udp.ReceiveAsync(cts.Token);
                string json = Encoding.UTF8.GetString(result.Buffer);

                using JsonDocument doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                string? server = root.TryGetProperty("server", out var sProp) ? sProp.GetString() : null;
                string? name = root.TryGetProperty("name", out var nProp) ? nProp.GetString() : null;

                if (!string.IsNullOrEmpty(server))
                {
                    _serverUrlBox.Text = server;
                    _urlStatusIcon.Text = "✓";
                    _urlStatusIcon.ForeColor = CloudPanColors.SuccessGreen;
                    _statusLabel.Text = "已找到服务端: " + (name ?? server);
                    _statusLabel.ForeColor = CloudPanColors.SuccessGreen;
                    _searchFound = true; // 阻止 TextChanged 重置状态
                    found = true;
                }
            }
            catch (OperationCanceledException) { /* 超时 —— 显示未找到 */ }
            catch (JsonException)
            {
                // 非 JSON 响应（可能来自其他设备广播），静默忽略
                System.Diagnostics.Debug.WriteLine("[SetupForm] 搜索收到非 JSON 响应");
            }
        }
        catch (SocketException)
        {
            errorMessage = "网络搜索异常，请检查防火墙或手动输入地址";
        }
        catch (Exception ex)
        {
            errorMessage = $"网络搜索异常: {ex.Message}";
        }
        finally
        {
            _searchAnimTimer.Stop();
            _searchButton.Text = "搜索局域网";
            _searchButton.Enabled = true;
            _progressBar.Visible = false;
            _isSearching = false;
        }

        if (found)
        {
            return; // 已设置成功状态
        }

        if (errorMessage != null)
        {
            _statusLabel.Text = errorMessage;
            _statusLabel.ForeColor = CloudPanColors.ErrorRed;
        }
        else
        {
            _statusLabel.Text = "未找到服务端。请在台式机上右键托盘图标 → 复制服务端地址并粘贴到上方";
            _statusLabel.ForeColor = CloudPanColors.WarningOrange;
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  字段消息辅助方法
    // ════════════════════════════════════════════════════════════════

    private enum MessageSeverity { Hint, Error }

    /// <summary>显示柔和提示（深灰色，用于实时校验和引导信息）。</summary>
    private static void ShowFieldHint(Label label, string text)
    {
        label.ForeColor = CloudPanColors.TextDarkGray;
        label.Text = text;
        label.Visible = true;
    }

    /// <summary>显示阻断错误（红色，用户提交时无效输入的反馈）。</summary>
    private static void ShowFieldError(Label label, string text)
    {
        label.ForeColor = CloudPanColors.ErrorRed;
        label.Text = text;
        label.Visible = true;
    }

    private static void ShowFieldMessage(Label label, string text, MessageSeverity severity)
    {
        if (severity == MessageSeverity.Error)
        {
            ShowFieldError(label, text);
        }
        else
        {
            ShowFieldHint(label, text);
        }
    }

    private static void HideFieldMessage(Label label)
    {
        label.Text = "";
        label.Visible = false;
    }

    // ════════════════════════════════════════════════════════════════
    //  浏览文件夹
    // ════════════════════════════════════════════════════════════════

    private void OnBrowseClick(object? sender, EventArgs e)
    {
        using FolderBrowserDialog d = new FolderBrowserDialog
        {
            SelectedPath = _syncRootBox.Text,
            ShowNewFolderButton = true,
        };
        if (d.ShowDialog() == DialogResult.OK)
        {
            _syncRootBox.Text = d.SelectedPath;
        }
    }
}
