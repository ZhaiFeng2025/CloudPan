using System.Net;
using CloudPan.Client.Core.Services;
using CloudPan.Infrastructure.Design;

namespace CloudPan.Client.UI;

/// <summary>SettingsForm 部分类：账户 Tab 页（服务端地址/同步文件夹/存储信息/Token）及测试连接、文件夹大小计算。</summary>
public partial class SettingsForm
{
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

        // T-053：测试连接结果白话文字（成功/失败原因+下一步），随图标一起反馈，不弹模态框
        _connResultText = new Label
        {
            Text = "",
            AutoSize = true,
            Margin = new Padding(4, 0, 0, 0),
            TextAlign = ContentAlignment.MiddleLeft,
            MaximumSize = new Size(300, 0),
        };
        serverRow.Controls.Add(_connResultText);

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
        // 清除上次连接结果反馈
        _connResultIcon.Text = "";
        _connResultText.Text = "";

        string url = _serverBox.Text.Trim().TrimEnd('/');
        if (string.IsNullOrEmpty(url))
        {
            _connResultIcon.Text = "✗";
            _connResultIcon.ForeColor = CloudPanColors.ErrorRed;
            _connResultText.Text = "请先输入服务端地址";
            _connResultText.ForeColor = CloudPanColors.ErrorRed;
            return;
        }

        _testConnBtn.Enabled = false;
        _testConnBtn.Text = "连接中...";
        try
        {
            // T-053：改走 ApiClient（唯一证书/代理/超时装配点），不再手建 HttpClient 拼 /api/health；
            // 自签证书静默接受 → 测试结果与真实同步连接一致，不自签服务端上假失败
            using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
            using ApiClient api = new(url, _tokenBox.Text.Trim());
            await api.EnsureHealthAsync(cts.Token);

            _connResultIcon.Text = "✓";
            _connResultIcon.ForeColor = CloudPanColors.SuccessGreen;
            _connResultText.Text = "连接成功，服务端正常运行";
            _connResultText.ForeColor = CloudPanColors.SuccessGreen;
        }
        catch (Exception ex)
        {
            (string reason, string nextStep) = ClassifyTestError(ex);
            _connResultIcon.Text = "✗";
            _connResultIcon.ForeColor = CloudPanColors.ErrorRed;
            _connResultText.Text = nextStep.Length == 0 ? reason : $"{reason}（{nextStep}）";
            _connResultText.ForeColor = CloudPanColors.ErrorRed;
        }
        finally
        {
            _testConnBtn.Enabled = true;
            _testConnBtn.Text = "测试连接";
        }
    }

    /// <summary>测试连接失败白话归因（ErrorAttribution 风格）：不透出裸状态码与底层异常原文。</summary>
    private static (string Reason, string NextStep) ClassifyTestError(Exception exception)
    {
        foreach (Exception leaf in Flatten(exception))
        {
            switch (leaf)
            {
                case HttpRequestException http when http.StatusCode == HttpStatusCode.NotFound:
                    return ("服务端地址不正确", "请检查地址是否完整，例如 http://192.168.1.100:8443");
                case HttpRequestException http when http.StatusCode is null:
                    return ("无法连接到服务端", "请确认台式机已开机、云盘服务正在运行");
                case TaskCanceledException:
                    return ("连接超时", "请检查网络，或确认地址与端口是否正确");
            }
        }
        return ("连接失败", "请检查地址与网络后重试");
    }

    /// <summary>递归解包 AggregateException 全部内层异常（CLAUDE.md 7.3，与 ErrorAttribution.Flatten 同语义）。</summary>
    private static IEnumerable<Exception> Flatten(Exception exception)
    {
        if (exception is AggregateException aggregate)
        {
            foreach (Exception inner in aggregate.Flatten().InnerExceptions)
            {
                foreach (Exception leaf in Flatten(inner))
                {
                    yield return leaf;
                }
            }
        }
        else
        {
            yield return exception;
        }
    }

    // ──────────────────────────────────────────────
    // 文件夹大小计算（5分钟缓存）
    // ──────────────────────────────────────────────

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
