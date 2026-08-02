using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Net.NetworkInformation;
using System.Security.Principal;
using CloudPan.Contract;
using CloudPan.Infrastructure.Design;
using CloudPan.Infrastructure.Security;

namespace CloudPan.Server.UI;

/// <summary>
/// 服务端安装向导——带界面的安装程序。
/// </summary>
public class ServerInstaller : Form
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
            ForeColor = Color.White,
            Font = new Font(CloudPanFonts.FontFamily, 16, FontStyle.Bold),
            AutoSize = true
        };
        Label subtitle = new Label
        {
            Text = "将在此计算机上安装文件同步服务",
            ForeColor = Color.White,
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
            ForeColor = Color.White,
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
            ForeColor = Color.White,
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
            ForeColor = Color.White,
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

    // =================================================================
    //  步骤指示器绘图
    // =================================================================
    private void StepPanel_Paint(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        string[] steps = new[] { "清理", "安装", "启动", "防火墙", "就绪" };
        int w = _stepPanel.Width;
        int startX = 30;
        int endX = w - 30;
        int stepW = endX - startX > 0 ? (endX - startX) / (steps.Length - 1) : 60;
        int cy = 11;  // 圆心 Y
        int r = 10;   // 半径
        int d = r * 2;

        for (int i = 0; i < steps.Length; i++)
        {
            int cx = startX + i * stepW;

            bool completed = _currentStep > i;
            bool current = _currentStep == i;

            Color circleColor, textColor;
            bool filled;

            if (completed || _currentStep >= steps.Length)
            {
                circleColor = CloudPanColors.SuccessGreen;
                textColor = CloudPanColors.SuccessGreen;
                filled = true;
            }
            else if (current)
            {
                circleColor = CloudPanColors.PrimaryBlue;
                textColor = CloudPanColors.PrimaryBlue;
                filled = true;
            }
            else
            {
                circleColor = CloudPanColors.BorderMid;
                textColor = CloudPanColors.TextMuted;
                filled = false;
            }

            // 连接线（到前一个步骤）
            if (i > 0)
            {
                int prevCx = startX + (i - 1) * stepW;
                // 前序步骤（i-1）已完成 = 连接线绿色；前序步骤是当前步骤 = 蓝色
                bool prevDone = _currentStep > i - 1 || _currentStep >= steps.Length;
                bool prevCurrent = _currentStep == i - 1;
                Color lineColor;
                if (prevDone)
                {
                    lineColor = CloudPanColors.SuccessGreen;
                }
                else if (prevCurrent)
                {
                    lineColor = CloudPanColors.PrimaryBlue;
                }
                else
                {
                    lineColor = CloudPanColors.BorderLight;
                }

                using Pen linePen = new Pen(lineColor, 2.5f);
                g.DrawLine(linePen, prevCx + r, cy, cx - r, cy);
            }

            // 圆
            if (filled)
            {
                using SolidBrush brush = new SolidBrush(circleColor);
                g.FillEllipse(brush, cx - r, cy - r, d, d);
            }
            else
            {
                using Pen pen = new Pen(circleColor, 2f);
                g.DrawEllipse(pen, cx - r, cy - r, d, d);
            }

            // 步骤编号
            using Font numFont = new Font(CloudPanFonts.FontFamily, 8f, FontStyle.Bold);
            string numText = (i + 1).ToString();
            var numSize = g.MeasureString(numText, numFont);
            using SolidBrush numBrush = new SolidBrush(filled ? Color.White : circleColor);
            g.DrawString(numText, numFont, numBrush,
                cx - numSize.Width / 2, cy - numSize.Height / 2);

            // 步骤标签
            using Font labelFont = new Font(CloudPanFonts.FontFamily, 7.5f,
                current ? FontStyle.Bold : FontStyle.Regular);
            var labelSize = g.MeasureString(steps[i], labelFont);
            using SolidBrush labelBrush = new SolidBrush(textColor);
            g.DrawString(steps[i], labelFont, labelBrush,
                cx - labelSize.Width / 2, cy + r + 3);
        }
    }

    /// <summary>
    /// 切换到指定步骤（0‑based），更新进度条并重绘步骤指示器
    /// </summary>
    private void SetStep(int stepIndex)
    {
        _currentStep = stepIndex;
        // 进度条最大值为 5（5 步：清理/安装/启动/防火墙/就绪），步骤 0-4 各占 20%
        _progressBar.Value = Math.Clamp(stepIndex + 1, 0, _progressBar.Maximum);
        _stepPanel.Invalidate();
    }

    /// <summary>
    /// 设置状态文字并自适应高度
    /// </summary>
    private void SetStatusText(string text)
    {
        _statusLabel.Text = text;
        int textWidth = _statusLabel.Width - _statusLabel.Padding.Horizontal;
        if (textWidth > 0)
        {
            var size = TextRenderer.MeasureText(text, _statusLabel.Font,
                new Size(textWidth, 0), TextFormatFlags.WordBreak);
            _statusLabel.Height = size.Height + _statusLabel.Padding.Vertical + 8;
        }
    }

    /// <summary>
    /// Token 显示成功动画：绿色边框闪烁 3 次后稳定
    /// </summary>
    private void FlashSuccessBorder()
    {
        _flashOriginalColor = _tokenBorder.BackColor;
        _flashColor = CloudPanColors.SuccessGreen;
        _flashCount = 0;
        System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = CloudPanEffects.DurationNormal };
        timer.Tick += FlashTimer_Tick;
        timer.Start();
    }

    /// <summary>闪烁动画 Timer 回调：交替显示原色/绿色，3 次后停在绿色并释放 Timer。</summary>
    private void FlashTimer_Tick(object? sender, EventArgs e)
    {
        _flashCount++;
        _tokenBorder.BackColor = _flashCount % 2 == 1 ? _flashColor : _flashOriginalColor;
        if (_flashCount >= 5) // 3 次闪烁后停在绿色
        {
            var timer = (System.Windows.Forms.Timer)sender!;
            timer.Stop();
            timer.Dispose();
            _tokenBorder.BackColor = _flashColor;
        }
    }

    // =================================================================
    //  安装流程
    // =================================================================
    private async Task InstallAsync()
    {
        _installBtn.Enabled = false;
        _progressBar.Visible = true;
        SetStatusText("正在安装服务...");
        _statusLabel.ForeColor = CloudPanColors.TextSecondary;

        try
        {
            // 同步目录输入验证
            string syncDir = _syncDirBox.Text.Trim();
            if (string.IsNullOrEmpty(syncDir))
            {
                syncDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "CloudPan");
            }
            else if (syncDir.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            {
                SetStatusText("同步目录路径包含非法字符，请重新输入。");
                _statusLabel.ForeColor = CloudPanColors.ErrorRed;
                _installBtn.Enabled = true;
                return;
            }

            // 检查是否已存在 .cloudpan 元数据目录（重复安装风险）
            string existingMeta = Path.Combine(syncDir, ".cloudpan");
            if (Directory.Exists(existingMeta))
            {
                var overwrite = MessageBox.Show(
                    $"同步目录「{syncDir}」中已存在 .cloudpan 元数据目录。\n\n" +
                    "是否继续安装？（如果之前安装过，继续可能导致数据混乱）",
                    "CloudPan — 元数据目录已存在",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (overwrite != DialogResult.Yes)
                {
                    SetStatusText("已取消安装。");
                    _installBtn.Enabled = true;
                    return;
                }
            }

            if (!Directory.Exists(syncDir))
            {
                Directory.CreateDirectory(syncDir);
            }

            // ========================================
            // 第一步：清理旧服务（忽略错误）
            // ========================================
            SetStep(0);
            SetStatusText("正在清理旧服务...");
            await Task.Run(() => RunExe("sc.exe", "stop", "CloudPanServer"));
            await Task.Run(() => RunExe("sc.exe", "delete", "CloudPanServer"));

            // ========================================
            // 第二步：创建新服务
            // ========================================
            SetStep(1);
            SetStatusText("正在创建服务...");
            string exePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "CloudPan.Server.exe");
            if (!File.Exists(exePath))
            {
                exePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Server", "CloudPan.Server.exe");
            }

            if (!File.Exists(exePath))
            {
                SetStatusText($"未找到服务可执行文件：{exePath}\n请确保程序文件完整，然后重试。");
                _statusLabel.ForeColor = CloudPanColors.ErrorRed;
                _progressBar.Visible = false;
                _installBtn.Enabled = true;
                return;
            }

            // 直接调用 sc.exe（绕过 cmd.exe），ArgumentList 自动处理特殊字符转义，消除命令注入风险
            bool createOk = await Task.Run(() =>
                RunExe("sc.exe", "create", "CloudPanServer",
                    "binPath=", $"\"{exePath}\" --SyncRoot {syncDir}",
                    "DisplayName=", "CloudPan 文件同步服务",
                    "start=", "auto"));
            if (!createOk)
            {
                string errDetail = GetLastCmdError() ?? "";
                SetStatusText($"创建服务失败：{errDetail}\n1. 是否以管理员身份运行\n2. 可执行文件路径是否正确\n然后点击「开始安装」重试");
                _statusLabel.ForeColor = CloudPanColors.ErrorRed;
                _progressBar.Visible = false;
                _installBtn.Enabled = true;
                return;
            }

            await Task.Run(() => RunExe("sc.exe", "description", "CloudPanServer", "CloudPan File Sync Service"));

            // M-04: 崩溃自动恢复
            await Task.Run(() =>
                RunExe("sc.exe", "failure", "CloudPanServer", "reset=86400", "actions=restart/5000/restart/10000/restart/60000"));

            // ========================================
            // 第三步：启动服务（失败则回滚）
            // ========================================
            SetStep(2);
            SetStatusText("正在启动服务...");
            bool startOk = await Task.Run(() => RunExe("sc.exe", "start", "CloudPanServer"));
            // 轮询等待服务进入 RUNNING 状态（首次启动可能因初始化延迟）
            bool serviceReady = false;
            for (int i = 0; i < 15; i++)
            {
                await Task.Delay(1000);
                bool queryOk = RunCmd("sc query CloudPanServer | find \"RUNNING\"");
                if (queryOk) { serviceReady = true; break; }
            }

            if (!startOk && !serviceReady)
            {
                string errDetail = GetLastCmdError() ?? "";
                SetStatusText($"服务启动失败：{errDetail}，正在回滚...");
                await Task.Run(() => RunExe("sc.exe", "stop", "CloudPanServer"));
                await Task.Run(() => RunExe("sc.exe", "delete", "CloudPanServer"));
                // 注意：防火墙规则尚未添加（步骤 3），此处不删除
                _statusLabel.ForeColor = CloudPanColors.ErrorRed;
                _progressBar.Visible = false;
                _installBtn.Enabled = true;
                return;
            }

            // ========================================
            // 第四步：防火墙规则（失败不阻塞）
            // ========================================
            SetStep(3);
            SetStatusText("正在添加防火墙规则...");
            await Task.Run(() => RunExe("netsh.exe", "advfirewall", "firewall", "delete", "rule", "name=CloudPan"));
            await Task.Run(() =>
                RunExe("netsh.exe", "advfirewall", "firewall", "add", "rule",
                    "name=CloudPan", "dir=in", "action=allow", "protocol=TCP", $"localport={SpecPorts.HttpPort}"));
            SetStatusText("已添加防火墙规则");

            // ========================================
            // 第五步：等待 Token 生成
            // ========================================
            SetStep(4);
            SetStatusText("等待服务初始化... (已等待 0 秒)");
            string tokenPath = Path.Combine(syncDir, ".cloudpan", "token.txt");

            for (int i = 0; i < 30; i++)
            {
                await Task.Delay(1000);
                SetStatusText($"等待服务初始化... (已等待 {i + 1} 秒)");
                if (File.Exists(tokenPath))
                {
                    string token;
                    try { token = SecretStore.ReadToken(syncDir) ?? ""; }
                    catch (IOException)
                    {
                        // 服务正在写入 token 文件，等待下一轮重试
                        continue;
                    }
                    if (!string.IsNullOrEmpty(token))
                    {
                        // 显示 Token 并触发成功动画
                        _tokenBox.Text = FormatToken(token);
                        _tokenArea.Visible = true;
                        SetStep(5); // 全部完成
                        _statusLabel.ForeColor = CloudPanColors.SuccessGreen;
                        string serverAddr = GetLocalIpAddress();
                        SetStatusText("安装成功！请在笔记本上运行 CloudPan.Client.exe");
                        AddServerAddressLabel(serverAddr, _tokenPanel);
                        FlashSuccessBorder();
                        break;
                    }
                }
            }

            if (!_tokenBox.IsDisposed && string.IsNullOrEmpty(_tokenBox.Text))
            {
                // 超时：不调用 SetStep(5)，保留步骤 4 蓝色状态，视觉上表明"未完成"
                SetStatusText($"安装完成但 Token 未就绪（Token 将在服务首次启动后生成）\n请稍后查看：{syncDir}\\.cloudpan\\token.txt");
                _statusLabel.ForeColor = CloudPanColors.WarningOrange;
            }

            _progressBar.Visible = false;
            _installBtn.Visible = false;
            _closeBtn.Visible = true;
            _closeBtn.Focus();
            DialogResult = DialogResult.OK; // 通知调用方安装成功，应退出进程避免端口冲突
        }
        catch (Exception ex)
        {
            SetStatusText($"安装失败: {ex.Message}\n已自动尝试回滚，请修复后重试。");
            _statusLabel.ForeColor = CloudPanColors.ErrorRed;
            await Task.Run(() => RunExe("sc.exe", "stop", "CloudPanServer"));
            await Task.Run(() => RunExe("sc.exe", "delete", "CloudPanServer"));
            // 防火墙规则可能尚未添加，delete 已幂等，留作清理
            await Task.Run(() => RunExe("netsh.exe", "advfirewall", "firewall", "delete", "rule", "name=CloudPan"));
            _progressBar.Visible = false;
            _installBtn.Enabled = true;
        }
    }


    // =================================================================
    //  辅助方法
    // =================================================================

    /// <summary>
    /// 获取本机局域网 IP 地址
    /// </summary>
    private static string GetLocalIpAddress()
    {
        try
        {
            var first = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                            n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .FirstOrDefault(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
            return first?.Address.ToString() ?? "127.0.0.1";
        }
        catch
        {
            return "127.0.0.1";
        }
    }

    /// <summary>
    /// 在 Token 面板中添加服务端地址显示
    /// </summary>
    private void AddServerAddressLabel(string ip, Panel panel)
    {
        Label addrLabel = new Label
        {
            Text = $"服务端地址: http://{ip}:{SpecPorts.HttpPort}",
            Location = new Point(10, 56),
            AutoSize = true,
            Font = new Font(CloudPanFonts.FontFamilyMono, CloudPanFonts.SizeMono),
            ForeColor = CloudPanColors.TextPrimary,
            Cursor = Cursors.Hand
        };
        Label hintLabel = new Label
        {
            Text = "（点击复制地址）",
            Location = new Point(10, 72),
            AutoSize = true,
            Font = new Font(CloudPanFonts.FontFamily, CloudPanFonts.SizeCaption),
            ForeColor = CloudPanColors.TextMuted
        };
        addrLabel.Tag = ip;
        addrLabel.Click += AddrLabel_Click;
        panel.Controls.Add(addrLabel);
        panel.Controls.Add(hintLabel);
    }

    /// <summary>点击服务端地址标签：复制地址并短暂显示"已复制"。</summary>
    private void AddrLabel_Click(object? sender, EventArgs e)
    {
        var addrLabel = (Label)sender!;
        string ip = addrLabel.Tag as string ?? "";
        try { Clipboard.SetText($"http://{ip}:{SpecPorts.HttpPort}"); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"复制地址失败: {ex.Message}"); }
        addrLabel.Text = $"服务端地址: http://{ip}:{SpecPorts.HttpPort} ✓ 已复制";
        Task.Run(async () =>
        {
            try { await Task.Delay(1500); } catch { }
            try
            {
                if (!addrLabel.IsDisposed)
                {
                    addrLabel.Invoke(() =>
                    {
                        if (!addrLabel.IsDisposed)
                        {
                            addrLabel.Text = $"服务端地址: http://{ip}:{SpecPorts.HttpPort}";
                        }
                    });
                }
            }
            catch (ObjectDisposedException) { }
        });
    }

    /// <summary>
    /// 将 Token 每 16 字符一组用短横分隔显示
    /// </summary>
    private static string FormatToken(string token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return token;
        }

        return string.Join("-",
            Enumerable.Range(0, (token.Length + 15) / 16)
                .Select(i => token.Substring(i * 16, Math.Min(16, token.Length - i * 16))));
    }

    /// <summary>
    /// 直接运行可执行文件（绕过 cmd.exe），从根本上消除命令注入风险。
    /// 使用 ProcessStartInfo.ArgumentList 自动处理参数转义。
    /// 失败时错误详情通过 LastRunCmdError 存储。
    /// </summary>
    private static bool RunExe(string exeName, params string[] args)
    {
        LastRunCmdError = null;
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo(exeName)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = false,
                RedirectStandardError = true
            };
            foreach (string a in args)
            {
                psi.ArgumentList.Add(a);
            }

            using Process? p = Process.Start(psi);
            if (p == null)
            {
                LastRunCmdError = $"无法启动进程: {exeName}";
                return false;
            }

            // 异步读取 stderr 避免管道缓冲区满导致子进程死锁。
            // 本地函数捕获局部 stderrOutput，同时满足 CP301（非匿名 lambda，可退订）。
            string? stderrOutput = null;
            void OnErrorDataReceived(object sender, DataReceivedEventArgs e)
            {
                if (e.Data != null)
                {
                    stderrOutput = (stderrOutput ?? "") + e.Data + "\n";
                }
            }
            p.ErrorDataReceived += OnErrorDataReceived;
            p.BeginErrorReadLine();

            if (!p.WaitForExit(30000))
            {
                p.Kill();
                try { p.WaitForExit(1000); } catch { }
                LastRunCmdError = $"命令执行超时(30s): {exeName} {string.Join(" ", args)}";
                return false;
            }

            // 等待异步读取完成（WaitForExit 不保证异步读取已全部到达）
            p.WaitForExit();

            if (p.ExitCode != 0)
            {
                string errTrimmed = !string.IsNullOrWhiteSpace(stderrOutput) ? stderrOutput.Trim() : "(无错误输出)";
                LastRunCmdError = $"exit={p.ExitCode}: {errTrimmed[..Math.Min(errTrimmed.Length, 120)]}";
            }

            return p.ExitCode == 0;
        }
        catch (Exception ex)
        {
            LastRunCmdError = $"{ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// 仅用于需要 cmd 管道（如 | find）的命令——参数必须硬编码、无用户输入。
    /// 切勿将用户可控的路径/文本传入此方法。
    /// </summary>
    private static bool RunCmd(string command)
    {
        LastRunCmdError = null;
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo("cmd.exe", "/c " + command)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = false,
                RedirectStandardError = true
            };
            using Process? p = Process.Start(psi);
            if (p == null)
            {
                return false;
            }

            if (!p.WaitForExit(30000))
            {
                p.Kill();
                try { p.WaitForExit(1000); } catch { }
                LastRunCmdError = $"命令执行超时(30s): {command}";
                return false;
            }

            string stderr = p.StandardError.ReadToEnd();

            if (p.ExitCode != 0)
            {
                string errTrimmed = !string.IsNullOrWhiteSpace(stderr) ? stderr.Trim() : "(无错误输出)";
                LastRunCmdError = $"exit={p.ExitCode}: {errTrimmed[..Math.Min(errTrimmed.Length, 120)]}";
            }

            return p.ExitCode == 0;
        }
        catch (Exception ex)
        {
            LastRunCmdError = $"{ex.Message}";
            return false;
        }
    }

    private static string? LastRunCmdError;

    /// <summary>获取最近一次命令调用的错误详情。</summary>
    private static string? GetLastCmdError() => LastRunCmdError;
}
