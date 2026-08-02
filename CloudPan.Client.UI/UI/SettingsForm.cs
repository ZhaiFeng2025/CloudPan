using CloudPan.Shared;

namespace CloudPan.Client.UI;

/// <summary>
/// 设置窗口——同步管理、账户配置（含存储信息）、带宽限制、选择性同步。
/// </summary>
public class SettingsForm : Form
{
    private readonly TabControl _tabs;
    private TextBox _serverBox = null!;
    private TextBox _folderBox = null!;
    private TextBox _tokenBox = null!;
    private TextBox _uploadLimitBox = null!;
    private TextBox _downloadLimitBox = null!;
    private SelectiveSyncPanel _syncPanel = null!;
    private Button _saveBtn = null!;
    private Button _testConnBtn = null!;
    private Label _connResultIcon = null!;
    private Button _tokenToggleBtn = null!;
    private Label _storageSizeLabel = null!;

    // 文件夹大小缓存（5分钟有效）
    private static long CachedSize;
    private static DateTime LastSizeCheck;
    private static string CachedPath = "";

    private bool _tokenMasked = true;

    public string ServerUrl => _serverBox.Text.Trim();
    public string SyncRoot => _folderBox.Text.Trim();
    public string Token => _tokenBox.Text.Trim();
    public long UploadLimitBps => long.TryParse(_uploadLimitBox.Text.Trim(), out long v) ? v * 1024 : 0;
    public long DownloadLimitBps => long.TryParse(_downloadLimitBox.Text.Trim(), out long v) ? v * 1024 : 0;
    public List<string> SelectedPaths => _syncPanel.SelectedPaths;

    public SettingsForm(string serverUrl, string syncRoot, string token, long uploadSpeedBps, long downloadSpeedBps, List<string> selectedPaths)
    {
        Text = "CloudPan 设置";
        Size = new Size(580, 560);
        StartPosition = FormStartPosition.CenterParent;
        Font = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;

        _tabs = new TabControl { Dock = DockStyle.Fill };

        BuildAccountTab(serverUrl, syncRoot, token);
        BuildBandwidthTab(uploadSpeedBps, downloadSpeedBps);
        BuildSyncTab(selectedPaths);
        BuildBottomPanel();

        Controls.Add(_tabs);

        // 异步计算文件夹大小
        _ = UpdateFolderSizeAsync(syncRoot);
    }

    // ──────────────────────────────────────────────
    // Tab 1: 账户（含存储信息）
    // ──────────────────────────────────────────────

    private void BuildAccountTab(string serverUrl, string syncRoot, string token)
    {
        TabPage accountTab = new TabPage("账户");
        TableLayoutPanel panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16),
            ColumnCount = 1,
            RowCount = 18,
        };
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // 0: 服务端地址标签
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 4));    // 1: 间距
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // 2: 输入行 + 测试按钮 + 结果图标
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // 3: 灰色提示
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 8));    // 4: 间距
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // 5: 同步文件夹标签
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 4));    // 6: 间距
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // 7: 文件夹输入
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 4));    // 8: 间距
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // 9: 存储占用（计算中...）
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // 10: 磁盘信息
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 12));   // 11: 间距（到Token）
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // 12: Token 标签
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 4));    // 13: 间距
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // 14: Token 输入（含显示/隐藏）
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // 15: 灰色提示
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 4));    // 16: 间距
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));   // 17: 弹性填充

        // ── Row 0: 服务端地址标签 ──
        panel.Controls.Add(new Label { Text = "服务端地址", AutoSize = true }, 0, 0);

        // ── Row 2: 地址输入行 + 测试按钮 + 结果图标 ──
        FlowLayoutPanel serverRow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };

        _serverBox = new TextBox
        {
            Text = serverUrl,
            Width = 340,
            PlaceholderText = "http://192.168.1.100:8443",
        };
        serverRow.Controls.Add(_serverBox);

        _testConnBtn = new Button
        {
            Text = "测试连接",
            Width = CloudPanSpacing.ButtonWidth,
            Height = CloudPanSpacing.InputHeight,
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand,
            Margin = new Padding(8, 0, 0, 0),
        };
        _testConnBtn.Click += TestConnection_Click;
        serverRow.Controls.Add(_testConnBtn);

        _connResultIcon = new Label
        {
            Text = "",
            AutoSize = true,
            Font = new Font("Segoe UI Symbol", 14F),
            Margin = new Padding(4, 0, 0, 0),
            TextAlign = ContentAlignment.MiddleCenter,
        };
        serverRow.Controls.Add(_connResultIcon);

        panel.Controls.Add(serverRow, 0, 2);

        // ── Row 3: 灰色提示 ──
        panel.Controls.Add(new Label
        {
            Text = "在台式机上运行的服务器地址",
            AutoSize = true,
            ForeColor = CloudPanColors.TextMuted,
        }, 0, 3);

        // ── Row 5: 同步文件夹标签 ──
        panel.Controls.Add(new Label { Text = "同步文件夹", AutoSize = true }, 0, 5);

        // ── Row 7: 文件夹输入框 ──
        _folderBox = new TextBox { Text = syncRoot, Dock = DockStyle.Fill };
        panel.Controls.Add(_folderBox, 0, 7);

        // ── Row 9: 存储大小（计算中占位）──
        _storageSizeLabel = new Label
        {
            Text = "占用: 计算中...",
            AutoSize = true,
            Font = new Font(CloudPanFonts.FontFamily, CloudPanFonts.SizeBody, FontStyle.Italic),
            ForeColor = CloudPanColors.TextMuted,
        };
        panel.Controls.Add(_storageSizeLabel, 0, 9);

        // ── Row 10: 磁盘信息与存储提示 ──
        FlowLayoutPanel storageInfo = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };

        string? rootPath = null;
        try { rootPath = Path.GetPathRoot(syncRoot); }
        catch { /* 无效路径，跳过磁盘信息 */ }

        if (!string.IsNullOrEmpty(rootPath))
        {
            try
            {
                DriveInfo drive = new DriveInfo(rootPath);
                string freeText = drive.AvailableFreeSpace > 1_073_741_824
                    ? $"{drive.AvailableFreeSpace / 1_073_741_824.0:F1} GB"
                    : $"{drive.AvailableFreeSpace / 1_048_576.0:F0} MB";

                storageInfo.Controls.Add(new Label
                {
                    Text = $"磁盘剩余空间: {freeText}",
                    AutoSize = true,
                    Margin = new Padding(0, 0, 0, 2),
                });
                storageInfo.Controls.Add(new Label
                {
                    Text = $"磁盘: {drive.Name} ({drive.DriveType})",
                    AutoSize = true,
                    ForeColor = CloudPanColors.TextMuted,
                    Margin = new Padding(0, 0, 0, 6),
                });
            }
            catch { /* 获取磁盘信息失败时忽略 */ }
        }

        storageInfo.Controls.Add(new Label
        {
            Text = "提示: 文件通过服务端共享，多设备同步会占用更多空间",
            AutoSize = true,
            ForeColor = CloudPanColors.TextMuted,
        });
        panel.Controls.Add(storageInfo, 0, 10);

        // ── Row 12: Token 标签 ──
        panel.Controls.Add(new Label
        {
            Text = "Token（修改后需重启客户端生效）",
            AutoSize = true,
        }, 0, 12);

        // ── Row 14: Token 输入框 + 显示/隐藏按钮 ──
        TableLayoutPanel tokenInputRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
        };
        tokenInputRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        tokenInputRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 62F));

        _tokenBox = new TextBox
        {
            Text = token,
            Dock = DockStyle.Fill,
            UseSystemPasswordChar = true,
        };

        _tokenToggleBtn = new Button
        {
            Text = "显示",
            Width = 58,
            Height = 26,
            FlatStyle = FlatStyle.Flat,
            BackColor = CloudPanColors.BackgroundLight,
            ForeColor = CloudPanColors.TextSecondary,
            Font = new Font(CloudPanFonts.FontFamily, CloudPanFonts.SizeBody),
            Cursor = Cursors.Hand,
            TextAlign = ContentAlignment.MiddleCenter,
            UseVisualStyleBackColor = false,
        };
        _tokenToggleBtn.FlatAppearance.BorderColor = CloudPanColors.BorderLight;
        _tokenToggleBtn.FlatAppearance.MouseOverBackColor = CloudPanColors.ButtonHoverBg;
        _tokenToggleBtn.FlatAppearance.MouseDownBackColor = CloudPanColors.ButtonPressBg;
        _tokenToggleBtn.Click += ToggleTokenMask;

        tokenInputRow.Controls.Add(_tokenBox, 0, 0);
        tokenInputRow.Controls.Add(_tokenToggleBtn, 1, 0);
        panel.Controls.Add(tokenInputRow, 0, 14);

        // ── Row 15: Token 提示 ──
        panel.Controls.Add(new Label
        {
            Text = "右键服务端托盘图标 → 复制 Token",
            AutoSize = true,
            ForeColor = CloudPanColors.TextMuted,
        }, 0, 15);

        accountTab.Controls.Add(panel);
        _tabs.TabPages.Add(accountTab);
    }

    // ──────────────────────────────────────────────
    // Tab 2: 带宽限制（含预设按钮）
    // ──────────────────────────────────────────────

    private void BuildBandwidthTab(long uploadSpeedBps, long downloadSpeedBps)
    {
        TabPage bwTab = new TabPage("带宽限制");
        FlowLayoutPanel bwPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16),
            FlowDirection = FlowDirection.TopDown,
        };

        // 上传限速
        bwPanel.Controls.Add(new Label
        {
            Text = "上传限速 (KB/s，0=不限速)",
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 2),
        });
        _uploadLimitBox = new TextBox
        {
            Text = (uploadSpeedBps / 1024).ToString(),
            Width = 120,
        };
        _uploadLimitBox.KeyPress += NumericOnly_KeyPress;
        bwPanel.Controls.Add(_uploadLimitBox);

        // 下载限速
        bwPanel.Controls.Add(new Label
        {
            Text = "下载限速 (KB/s，0=不限速)",
            AutoSize = true,
            Margin = new Padding(0, 10, 0, 2),
        });
        _downloadLimitBox = new TextBox
        {
            Text = (downloadSpeedBps / 1024).ToString(),
            Width = 120,
        };
        _downloadLimitBox.KeyPress += NumericOnly_KeyPress;
        bwPanel.Controls.Add(_downloadLimitBox);

        // 预设按钮
        bwPanel.Controls.Add(new Label
        {
            Text = "快捷设置",
            AutoSize = true,
            Margin = new Padding(0, 12, 0, 4),
            Font = new Font(CloudPanFonts.FontFamily, 9F, FontStyle.Bold),
        });

        FlowLayoutPanel presetRow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };

        (string text, int kbValue)[] presets = new (string text, int kbValue)[]
        {
            ("不限速", 0),
            ("1MB/s", 1024),
            ("5MB/s", 5120),
            ("10MB/s", 10240),
        };

        foreach (var (text, kbValue) in presets)
        {
            Button btn = new Button
            {
                Text = text,
                Width = 72,
                Height = 26,
                FlatStyle = FlatStyle.Flat,
                BackColor = CloudPanColors.BackgroundLight,
                ForeColor = CloudPanColors.TextSecondary,
                Font = new Font(CloudPanFonts.FontFamily, CloudPanFonts.SizeBody),
                Cursor = Cursors.Hand,
                UseVisualStyleBackColor = false,
            };
            btn.FlatAppearance.BorderColor = CloudPanColors.BorderLight;
            btn.FlatAppearance.MouseOverBackColor = CloudPanColors.ButtonHoverBg;
            btn.FlatAppearance.MouseDownBackColor = CloudPanColors.ButtonPressBg;

            // 值经 Tag 传递到具名处理器（CP301：避免捕获循环变量的匿名 lambda）
            btn.Tag = kbValue;
            btn.Click += PresetBtn_Click;

            presetRow.Controls.Add(btn);
        }

        bwPanel.Controls.Add(presetRow);
        bwTab.Controls.Add(bwPanel);
        _tabs.TabPages.Add(bwTab);
    }

    // ──────────────────────────────────────────────
    // 具名事件处理器（CP301：避免匿名 lambda 订阅无法退订）
    // ──────────────────────────────────────────────

    /// <summary>预设限速按钮：把按钮 Tag 中的值（KB/s）应用到上下行输入框。</summary>
    private void PresetBtn_Click(object? sender, EventArgs e)
    {
        if (sender is Button btn && btn.Tag is int value)
        {
            _uploadLimitBox.Text = value.ToString();
            _downloadLimitBox.Text = value.ToString();
        }
    }

    private void SaveBtn_Click(object? sender, EventArgs e)
    {
        DialogResult = DialogResult.OK;
        Close();
    }

    private void CancelBtn_Click(object? sender, EventArgs e)
    {
        DialogResult = DialogResult.Cancel;
        Close();
    }

    // ──────────────────────────────────────────────
    // Tab 3: 选择性同步
    // ──────────────────────────────────────────────

    private void BuildSyncTab(List<string>? selectedPaths)
    {
        TabPage syncTab = new TabPage("选择性同步");
        _syncPanel = new SelectiveSyncPanel();
        if (selectedPaths != null)
        {
            _syncPanel.SelectedPaths = selectedPaths;
        }

        syncTab.Controls.Add(_syncPanel);
        _tabs.TabPages.Add(syncTab);
    }

    // ──────────────────────────────────────────────
    // 底部按钮
    // ──────────────────────────────────────────────

    private void BuildBottomPanel()
    {
        TableLayoutPanel bottomPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 48,
            ColumnCount = 2,
            RowCount = 1,
        };
        bottomPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        bottomPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        Label saveHint = new Label
        {
            Text = "提示：Token 修改需重启客户端后生效",
            AutoSize = true,
            ForeColor = CloudPanColors.TextMuted,
            Font = new Font(SystemFonts.MessageBoxFont?.FontFamily ?? FontFamily.GenericSansSerif, 8F),
            Margin = new Padding(16, 0, 0, 0),
            TextAlign = ContentAlignment.MiddleLeft,
        };

        FlowLayoutPanel btnPanel = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            Height = 48,
            Padding = new Padding(12),
        };

        // 保存按钮（蓝色主色）
        _saveBtn = new Button
        {
            Text = "保存",
            Width = CloudPanSpacing.ButtonWidth,
            Height = CloudPanSpacing.InputHeight,
            FlatStyle = FlatStyle.Flat,
            BackColor = CloudPanColors.PrimaryBlue,
            ForeColor = Color.White,
            Font = new Font(CloudPanFonts.FontFamily, 10F, FontStyle.Bold),
            Cursor = Cursors.Hand,
            UseVisualStyleBackColor = false,
        };
        _saveBtn.FlatAppearance.BorderSize = 0;
        _saveBtn.FlatAppearance.MouseOverBackColor = CloudPanColors.PrimaryBlueHover;
        _saveBtn.FlatAppearance.MouseDownBackColor = CloudPanColors.PrimaryBluePress;
        _saveBtn.Click += SaveBtn_Click;

        // 取消按钮（与 SetupForm 样式一致）
        Button cancelBtn = new Button
        {
            Text = "取消",
            Width = CloudPanSpacing.ButtonWidth,
            Height = CloudPanSpacing.InputHeight,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.White,
            ForeColor = CloudPanColors.TextSecondary,
            Font = new Font(CloudPanFonts.FontFamily, CloudPanFonts.SizeBody),
            Cursor = Cursors.Hand,
            UseVisualStyleBackColor = false,
        };
        cancelBtn.FlatAppearance.BorderColor = CloudPanColors.BorderLight;
        cancelBtn.FlatAppearance.MouseOverBackColor = CloudPanColors.ButtonHoverBg;
        cancelBtn.FlatAppearance.MouseDownBackColor = CloudPanColors.ButtonPressBg;
        cancelBtn.Click += CancelBtn_Click;

        btnPanel.Controls.Add(_saveBtn);
        btnPanel.Controls.Add(cancelBtn);

        bottomPanel.Controls.Add(saveHint, 0, 0);
        bottomPanel.Controls.Add(btnPanel, 1, 0);

        Controls.Add(bottomPanel);
    }

    // ──────────────────────────────────────────────
    // Token 显示/隐藏
    // ──────────────────────────────────────────────

    private void ToggleTokenMask(object? sender, EventArgs e)
    {
        _tokenMasked = !_tokenMasked;
        _tokenBox.UseSystemPasswordChar = _tokenMasked;
        _tokenToggleBtn.Text = _tokenMasked ? "显示" : "隐藏";
        _tokenBox.Select(_tokenBox.TextLength, 0);
    }

    // ──────────────────────────────────────────────
    // 测试连接
    // ──────────────────────────────────────────────

    private async void TestConnection_Click(object? sender, EventArgs e)
    {
        // 清除上次连接结果图标
        _connResultIcon.Text = "";

        string url = _serverBox.Text.Trim().TrimEnd('/');
        if (string.IsNullOrEmpty(url))
        {
            MessageBox.Show(this, "请先输入服务端地址", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        _testConnBtn.Enabled = false;
        _testConnBtn.Text = "连接中...";
        try
        {
            using HttpClient httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var response = await httpClient.GetAsync($"{url}/api/health");
            if (response.IsSuccessStatusCode)
            {
                _connResultIcon.Text = "✓";
                _connResultIcon.ForeColor = CloudPanColors.SuccessGreen;
                MessageBox.Show(this, "连接成功！服务端正常运行。", "测试连接", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                _connResultIcon.Text = "✗";
                _connResultIcon.ForeColor = CloudPanColors.ErrorRed;
                MessageBox.Show(this, $"服务端返回状态码: {(int)response.StatusCode}", "连接失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        catch (Exception ex)
        {
            _connResultIcon.Text = "✗";
            _connResultIcon.ForeColor = CloudPanColors.ErrorRed;
            MessageBox.Show(this, $"无法连接: {ex.Message}", "连接失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _testConnBtn.Enabled = true;
            _testConnBtn.Text = "测试连接";
        }
    }

    // ──────────────────────────────────────────────
    // 文件夹大小计算（5分钟缓存）
    // ──────────────────────────────────────────────

    /// <summary>只允许数字输入，退格和方向键除外。</summary>
    private static void NumericOnly_KeyPress(object? sender, KeyPressEventArgs e)
    {
        if (!char.IsControl(e.KeyChar) && !char.IsDigit(e.KeyChar))
        {
            e.Handled = true;
        }
    }

    /// <summary>异步获取文件夹大小，含5分钟缓存。</summary>
    private static async Task<long> GetFolderSizeAsync(string path)
    {
        // 5分钟缓存命中（同时校验路径一致）
        if (!string.IsNullOrEmpty(path) && path == CachedPath && (DateTime.UtcNow - LastSizeCheck).TotalMinutes < 5)
        {
            return CachedSize;
        }

        long size = await Task.Run(() =>
        {
            try
            {
                long size = 0;
                foreach (string f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    try { size += new FileInfo(f).Length; } catch { }
                    if (size > 1_000_000_000_000)
                    {
                        break; // 1TB 截断
                    }
                }
                return size;
            }
            catch { return 0L; }
        });

        CachedSize = size;
        LastSizeCheck = DateTime.UtcNow;
        CachedPath = path;
        return size;
    }

    /// <summary>异步更新存储标签页的文件夹大小显示。</summary>
    private async Task UpdateFolderSizeAsync(string syncRoot)
    {
        if (string.IsNullOrEmpty(syncRoot))
        {
            return;
        }

        long syncSize = await GetFolderSizeAsync(syncRoot);
        string usedText = syncSize > 1_073_741_824
            ? $"{syncSize / 1_073_741_824.0:F1} GB"
            : $"{syncSize / 1_048_576.0:F0} MB";

        // 计算完成后平滑替换——恢复粗体深色
        _storageSizeLabel.ForeColor = CloudPanColors.TextSecondary;
        _storageSizeLabel.Font = new Font(CloudPanFonts.FontFamily, CloudPanFonts.SizeBody, FontStyle.Bold);
        _storageSizeLabel.Text = $"占用: {usedText}";
    }

    /// <summary>旧的同步 GetFolderSize（已弃用，保留兼容）。</summary>
    [Obsolete("请使用异步版本 GetFolderSizeAsync")]
    private static long GetFolderSize(string path)
    {
        try
        {
            long size = 0;
            foreach (string f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try { size += new FileInfo(f).Length; } catch { }
                if (size > 1_000_000_000_000)
                {
                    break;
                }
            }
            return size;
        }
        catch { return 0; }
    }
}
