using System.Net;
using System.Net.Sockets;
using CloudPan.Infrastructure.Configuration;
using CloudPan.Infrastructure.Design;
using CloudPan.Server.Core;
using CloudPan.Server.Host.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace CloudPan.Server.UI;

/// <summary>
/// 服务端设置页（管理窗口"设置"页签）。
/// 三个设置项：端口（重启生效）、同步根目录（重启 + 迁移警告）、Token 轮换（立即生效）。
/// </summary>
public class SettingsPage : UserControl
{
    private readonly ITokenService _tokenService;
    private readonly Action<string> _log;
    private readonly int _effectivePort;
    private readonly string _currentSyncRoot;

    private readonly TextBox _portBox;
    private readonly TextBox _syncRootBox;
    private readonly TextBox _tokenBox;
    private readonly CheckBox _disconnectCheck;
    private readonly Button _toggleTokenBtn;
    private readonly Button _rotateBtn;
    private readonly Label _statusLabel;

    public SettingsPage(
        IServiceProvider services,
        int effectivePort,
        string currentSyncRoot,
        Action<string> log)
    {
        _tokenService = services.GetRequiredService<ITokenService>();
        _effectivePort = effectivePort;
        _currentSyncRoot = currentSyncRoot;
        _log = log;

        BackColor = CloudPanColors.BackgroundWhite;
        AutoScroll = true;
        Padding = new Padding(CloudPanSpacing.MarginStandard);

        TableLayoutPanel root = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));

        int row = 0;

        // ===== 网络区 =====
        AddSectionTitle(root, ref row, "网络");

        _portBox = new TextBox
        {
            Text = effectivePort.ToString(),
            Width = 120,
            Height = CloudPanSpacing.InputHeight,
            Font = new Font(CloudPanFonts.FontFamily, CloudPanFonts.SizeBody),
            BackColor = CloudPanColors.BackgroundWhite
        };
        _portBox.KeyPress += NumericOnly_KeyPress;
        AddFieldRow(root, ref row, "服务端口", _portBox);
        AddHint(root, ref row, "重启服务后生效", CloudPanColors.TextMuted);

        // ===== 存储区 =====
        AddSectionTitle(root, ref row, "存储");

        Panel syncRootRow = new Panel { Dock = DockStyle.Fill, Height = CloudPanSpacing.InputHeight };
        _syncRootBox = new TextBox
        {
            Text = currentSyncRoot,
            Dock = DockStyle.Fill,
            Font = new Font(CloudPanFonts.FontFamily, CloudPanFonts.SizeBody),
            BackColor = CloudPanColors.BackgroundWhite
        };
        Button browseBtn = new Button
        {
            Text = "浏览...",
            FlatStyle = FlatStyle.Flat,
            Width = 80,
            Height = CloudPanSpacing.InputHeight,
            Dock = DockStyle.Right,
            Cursor = Cursors.Hand
        };
        browseBtn.Click += BrowseBtn_Click;
        syncRootRow.Controls.Add(_syncRootBox);
        syncRootRow.Controls.Add(browseBtn);
        AddFieldRow(root, ref row, "同步根目录", syncRootRow);
        AddHint(root, ref row, "修改后需重启；旧目录 .cloudpan 数据不会自动迁移", CloudPanColors.WarningOrange);

        // ===== 安全区 =====
        AddSectionTitle(root, ref row, "安全");

        _tokenBox = new TextBox
        {
            ReadOnly = true,
            UseSystemPasswordChar = true,
            Width = 320,
            Height = CloudPanSpacing.InputHeight,
            Font = new Font(CloudPanFonts.FontFamilyMono, CloudPanFonts.SizeMono),
            BackColor = CloudPanColors.BackgroundGray
        };
        _toggleTokenBtn = new Button
        {
            Text = "显示",
            FlatStyle = FlatStyle.Flat,
            Width = 60,
            Height = CloudPanSpacing.InputHeight,
            Cursor = Cursors.Hand,
            Margin = new Padding(6, 0, 0, 0)
        };
        _toggleTokenBtn.Click += ToggleTokenMask_Click;

        Button copyTokenBtn = new Button
        {
            Text = "复制",
            FlatStyle = FlatStyle.Flat,
            Width = 60,
            Height = CloudPanSpacing.InputHeight,
            Cursor = Cursors.Hand,
            Margin = new Padding(6, 0, 0, 0)
        };
        copyTokenBtn.Click += CopyToken_Click;

        Panel tokenRow = new Panel { Dock = DockStyle.Fill, Height = CloudPanSpacing.InputHeight };
        tokenRow.Controls.Add(copyTokenBtn);
        tokenRow.Controls.Add(_toggleTokenBtn);
        tokenRow.Controls.Add(_tokenBox);
        copyTokenBtn.Dock = DockStyle.Right;
        _toggleTokenBtn.Dock = DockStyle.Right;
        _tokenBox.Dock = DockStyle.Fill;
        AddFieldRow(root, ref row, "家庭共享 Token", tokenRow);

        _disconnectCheck = new CheckBox
        {
            Text = "同时断开所有已连接设备",
            AutoSize = true,
            ForeColor = CloudPanColors.TextSecondary,
            Font = new Font(CloudPanFonts.FontFamily, CloudPanFonts.SizeBodySmall)
        };
        _rotateBtn = new Button
        {
            Text = "轮换 Token",
            FlatStyle = FlatStyle.Flat,
            BackColor = CloudPanColors.ErrorBgLight,
            ForeColor = CloudPanColors.ErrorRed,
            Width = 110,
            Height = 32,
            Cursor = Cursors.Hand
        };
        _rotateBtn.Click += RotateBtn_Click;
        root.Controls.Add(_rotateBtn, 0, row);
        root.Controls.Add(_disconnectCheck, 1, row);
        row++;
        AddHint(root, ref row, "轮换后所有客户端需使用新 Token 重新配置", CloudPanColors.TextMuted);

        // ===== 底部操作区 =====
        Button saveBtn = new Button
        {
            Text = "保存设置",
            FlatStyle = FlatStyle.Flat,
            BackColor = CloudPanColors.PrimaryBlue,
            ForeColor = CloudPanColors.TextOnPrimary,
            Width = CloudPanSpacing.ButtonWidth,
            Height = 34,
            Cursor = Cursors.Hand,
            Margin = new Padding(0, CloudPanSpacing.ElementSpacing, 0, 0)
        };
        saveBtn.Click += SaveBtn_Click;
        root.Controls.Add(saveBtn, 0, row);

        _statusLabel = new Label
        {
            AutoSize = true,
            Font = new Font(CloudPanFonts.FontFamily, CloudPanFonts.SizeBodySmall),
            ForeColor = CloudPanColors.TextSecondary,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(12, 0, 0, 0)
        };
        root.Controls.Add(_statusLabel, 1, row);
        row++;

        Controls.Add(root);

        Load += SettingsPage_Load;
    }

    // ===== 数据加载 =====

    private async void SettingsPage_Load(object? sender, EventArgs e)
    {
        try
        {
            string? token = await _tokenService.GetCurrentTokenAsync();
            if (!string.IsNullOrEmpty(token))
            {
                _tokenBox.Text = token;
            }
        }
        catch (Exception ex)
        {
            SetStatus($"读取 Token 失败: {ex.Message}", CloudPanColors.ErrorRed);
        }
    }

    // ===== 布局辅助 =====

    private static void AddSectionTitle(TableLayoutPanel root, ref int row, string title)
    {
        Label lbl = new Label
        {
            Text = title,
            Font = new Font(CloudPanFonts.FontFamily, CloudPanFonts.SizeSubtitle, FontStyle.Bold),
            ForeColor = CloudPanColors.TextPrimary,
            AutoSize = true,
            Margin = new Padding(0, CloudPanSpacing.GroupSpacing, 0, CloudPanSpacing.ElementSpacing)
        };
        root.Controls.Add(lbl, 0, row);
        root.SetColumnSpan(lbl, 2);
        row++;
    }

    private static void AddFieldRow(TableLayoutPanel root, ref int row, string label, Control field)
    {
        Label lbl = new Label
        {
            Text = label,
            AutoSize = true,
            Font = new Font(CloudPanFonts.FontFamily, CloudPanFonts.SizeBody),
            ForeColor = CloudPanColors.TextSecondary,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 8, 12, 0)
        };
        root.Controls.Add(lbl, 0, row);
        root.Controls.Add(field, 1, row);
        row++;
    }

    private static void AddHint(TableLayoutPanel root, ref int row, string text, Color color)
    {
        Label lbl = new Label
        {
            Text = text,
            AutoSize = true,
            Font = new Font(CloudPanFonts.FontFamily, CloudPanFonts.SizeBodySmall),
            ForeColor = color,
            Margin = new Padding(0, 2, 0, 0)
        };
        root.Controls.Add(lbl, 1, row);
        row++;
    }

    private void SetStatus(string text, Color color)
    {
        _statusLabel.Text = text;
        _statusLabel.ForeColor = color;
    }

    // ===== 端口校验 =====

    private static void NumericOnly_KeyPress(object? sender, KeyPressEventArgs e)
    {
        if (!char.IsDigit(e.KeyChar) && !char.IsControl(e.KeyChar))
        {
            e.Handled = true;
        }
    }

    /// <summary>探测端口是否被占用（不含当前生效端口——本进程正在监听）。</summary>
    private static bool IsPortInUse(int port, int currentEffectivePort)
    {
        if (port == currentEffectivePort)
        {
            return false;
        }
        try
        {
            var listener = new TcpListener(IPAddress.Any, port);
            listener.Start();
            listener.Stop();
            return false;
        }
        catch (SocketException)
        {
            return true;
        }
    }

    // ===== 事件处理 =====

    private void BrowseBtn_Click(object? sender, EventArgs e)
    {
        using FolderBrowserDialog dialog = new FolderBrowserDialog
        {
            Description = "选择同步根目录",
            SelectedPath = Directory.Exists(_syncRootBox.Text) ? _syncRootBox.Text : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            UseDescriptionForTitle = true
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _syncRootBox.Text = dialog.SelectedPath;
        }
    }

    private void ToggleTokenMask_Click(object? sender, EventArgs e)
    {
        _tokenBox.UseSystemPasswordChar = !_tokenBox.UseSystemPasswordChar;
        _toggleTokenBtn.Text = _tokenBox.UseSystemPasswordChar ? "显示" : "隐藏";
    }

    private void CopyToken_Click(object? sender, EventArgs e)
    {
        if (string.IsNullOrEmpty(_tokenBox.Text))
        {
            SetStatus("Token 尚未生成", CloudPanColors.WarningOrange);
            return;
        }
        try { Clipboard.SetText(_tokenBox.Text); SetStatus("Token 已复制到剪贴板", CloudPanColors.SuccessGreen); }
        catch (Exception ex) { SetStatus($"复制失败: {ex.Message}", CloudPanColors.ErrorRed); }
    }

    private async void RotateBtn_Click(object? sender, EventArgs e)
    {
        var result = MessageBox.Show(
            "将重新生成家庭共享 Token，所有客户端需使用新 Token 重新配置。\n\n确定继续吗？",
            "轮换 Token", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
        if (result != DialogResult.OK)
        {
            return;
        }

        _rotateBtn.Enabled = false;
        try
        {
            string newToken = await _tokenService.RotateAsync(_disconnectCheck.Checked);
            _tokenBox.Text = newToken;
            ServerTrayApp.Token = newToken;
            _log("Token 已轮换（旧 Token 已失效）");
            SetStatus("轮换成功，旧 Token 已失效", CloudPanColors.SuccessGreen);
        }
        catch (Exception ex)
        {
            _log($"Token 轮换失败: {ex.Message}");
            SetStatus($"轮换失败: {ex.Message}", CloudPanColors.ErrorRed);
        }
        finally
        {
            _rotateBtn.Enabled = true;
        }
    }

    private void SaveBtn_Click(object? sender, EventArgs e)
    {
        // 1. 端口校验
        if (!int.TryParse(_portBox.Text.Trim(), out int newPort) || newPort < 1 || newPort > 65535)
        {
            SetStatus("端口必须是 1-65535 的整数", CloudPanColors.ErrorRed);
            return;
        }
        if (IsPortInUse(newPort, _effectivePort))
        {
            SetStatus($"端口 {newPort} 已被占用，请更换端口", CloudPanColors.ErrorRed);
            return;
        }

        // 2. 同步根目录校验
        string newSyncRoot = _syncRootBox.Text.Trim();
        string fullNewRoot;
        try
        {
            if (string.IsNullOrWhiteSpace(newSyncRoot))
            {
                throw new ArgumentException("同步根目录不能为空");
            }
            fullNewRoot = Path.GetFullPath(newSyncRoot);
        }
        catch (Exception ex)
        {
            SetStatus($"同步根目录无效: {ex.Message}", CloudPanColors.ErrorRed);
            return;
        }

        bool syncRootChanged = !string.Equals(
            Path.GetFullPath(_currentSyncRoot), fullNewRoot, StringComparison.OrdinalIgnoreCase);
        if (syncRootChanged)
        {
            var warning = MessageBox.Show(
                "更改同步根目录后，新目录将从空开始重新同步（新数据库、新 Token）。\n\n" +
                "旧目录中的 .cloudpan（数据库/版本历史/Token）不会被迁移或删除。\n\n" +
                "确定继续吗？",
                "更改同步根目录", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
            if (warning != DialogResult.OK)
            {
                return;
            }

            // 旧安装的 binPath 带 --SyncRoot 会覆盖设置文件——提示重装迁移
            if (TrayAppRunner.IsServiceInstalled("CloudPanServer")
                && ServiceRestartHelper.ServiceHasLegacyBinPathParam("--SyncRoot"))
            {
                MessageBox.Show(
                    "检测到服务启动参数含旧的 --SyncRoot，会覆盖此处设置的同步根目录。\n\n" +
                    "请重新运行安装向导（或删除后重装 CloudPanServer 服务），以让新目录生效。",
                    "服务配置提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        // 3. 保存
        try
        {
            ServerSettingsFile.Save(new BootstrapSettings(newPort, fullNewRoot));
            SetStatus("已保存，重启服务后生效", CloudPanColors.SuccessGreen);
            _log($"设置已保存：端口 {newPort}，同步根目录 {fullNewRoot}");
            ServiceRestartHelper.PromptRestart();
        }
        catch (Exception ex)
        {
            SetStatus($"保存失败: {ex.Message}", CloudPanColors.ErrorRed);
        }
    }

}
