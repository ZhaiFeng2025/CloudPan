using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using CloudPan.Client.Core.Services;
using CloudPan.Contract;
using CloudPan.Infrastructure.Design;

namespace CloudPan.Client.UI;

/// <summary>
/// 冲突解决对话框（T-070 拆分）：非模态版本对比 + 白话选项（保留两者/本机/服务端）。
/// 由 MainWindow 在冲突列表中逐条点开调用，回调 MainWindow.ResolveConflict 完成解决。
/// </summary>
internal static class ConflictResolutionDialog
{
    /// <summary>
    /// 显示冲突解决对话框——非模态（批量冲突只弹聚合列表，此处由用户逐条点开）。
    /// 选项配白话解释，默认「保留两者」（安全，不丢任何内容），提供「对比两个版本」。
    /// </summary>
    public static void Show(MainWindow window, SyncEngine engine, ConflictInfo conflict)
    {
        string fileName = Path.GetFileName(conflict.RelativePath);
        string localTime = conflict.LocalModifiedTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? "未知";
        string localSizeStr = UiFormat.FormatFileSize(conflict.LocalFileSize);
        string remoteTime = conflict.RemoteModifiedTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? "未知";
        string remoteSizeStr = conflict.RemoteFileSize.HasValue ? UiFormat.FormatFileSize(conflict.RemoteFileSize.Value) : "未知";

        Form dialog = new Form
        {
            Text = $"文件冲突 — {fileName}",
            Size = new Size(620, 440),
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
        };

        TableLayoutPanel layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16),
            RowCount = 5,
            ColumnCount = 1,
        };

        // 标题
        layout.Controls.Add(new Label
        {
            Text = $"「{fileName}」在本机和云盘上同时发生了修改",
            Font = new Font((SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont).FontFamily, 10f, FontStyle.Bold),
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 8),
        }, 0, 0);

        // 本机版本（蓝左边框 + 浅蓝背景）
        layout.Controls.Add(BuildVersionPanel(
            CloudPanColors.AccentBlue, CloudPanColors.InfoBgLight,
            $"本机版本   修改时间: {localTime}    大小: {localSizeStr}"), 0, 1);

        // 云盘版本（绿左边框 + 浅绿背景）—— T-036：远程时间/大小为真实值，不再恒「未知」
        layout.Controls.Add(BuildVersionPanel(
            CloudPanColors.SuccessGreen, CloudPanColors.SuccessBgLight,
            $"云盘版本   修改时间: {remoteTime}    大小: {remoteSizeStr}"), 0, 2);

        // 提示文字
        layout.Controls.Add(new Label
        {
            Text = "请选择处理方式（推荐第 1 项，不会丢任何内容）：",
            AutoSize = true,
            Margin = new Padding(0, 8, 0, 4),
        }, 0, 3);

        // 选项面板：按钮（左） + 白话解释（右，可换行）
        TableLayoutPanel optionPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 4,
            Margin = new Padding(0, 0, 0, 8),
        };
        optionPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        optionPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        Button btnBoth = new Button
        {
            Text = "★ 保留两者（推荐）",
            Height = 32,
            Dock = DockStyle.Fill,
            FlatStyle = FlatStyle.Flat,
        };
        btnBoth.FlatAppearance.BorderColor = CloudPanColors.WarningOrange;
        void OnKeepBothClick(object? s, EventArgs e)
        {
            dialog.Close();
            window.ResolveConflict(conflict, ConflictResolution.KeepBoth);
        }
        btnBoth.Click += OnKeepBothClick;
        optionPanel.Controls.Add(btnBoth, 0, 0);
        optionPanel.Controls.Add(BuildOptionDesc("本机文件自动改名保留，云盘上的最新版本下载回原位——两边都不丢"), 1, 0);

        Button btnLocal = new Button
        {
            Text = "保留本机版本",
            Height = 32,
            Dock = DockStyle.Fill,
            FlatStyle = FlatStyle.Flat,
        };
        btnLocal.FlatAppearance.BorderColor = CloudPanColors.AccentBlue;
        void OnKeepLocalClick(object? s, EventArgs e)
        {
            dialog.Close();
            window.ResolveConflict(conflict, ConflictResolution.KeepLocal);
        }
        btnLocal.Click += OnKeepLocalClick;
        optionPanel.Controls.Add(btnLocal, 0, 1);
        optionPanel.Controls.Add(BuildOptionDesc("用本机的改动覆盖云盘（云盘上的改动会丢失）"), 1, 1);

        Button btnRemote = new Button
        {
            Text = "保留服务端版本",
            Height = 32,
            Dock = DockStyle.Fill,
            FlatStyle = FlatStyle.Flat,
        };
        btnRemote.FlatAppearance.BorderColor = CloudPanColors.SuccessGreen;
        void OnKeepRemoteClick(object? s, EventArgs e)
        {
            dialog.Close();
            window.ResolveConflict(conflict, ConflictResolution.KeepRemote);
        }
        btnRemote.Click += OnKeepRemoteClick;
        optionPanel.Controls.Add(btnRemote, 0, 2);
        optionPanel.Controls.Add(BuildOptionDesc("用云盘上的版本覆盖本机（本机的改动会丢失）"), 1, 2);

        Button btnCompare = new Button
        {
            Text = "对比两个版本",
            Height = 32,
            Dock = DockStyle.Fill,
            FlatStyle = FlatStyle.Flat,
        };
        btnCompare.FlatAppearance.BorderColor = CloudPanColors.ButtonBorderGray;
        // async void 仅用于 UI 事件处理器，顶层 try-catch 覆盖整个方法体（CLAUDE.md 7.2）
        async void OnCompareVersionsClick(object? s, EventArgs e)
        {
            try
            {
                string localPath = conflict.LocalPath;
                if (File.Exists(localPath))
                {
                    Process.Start(new ProcessStartInfo(localPath) { UseShellExecute = true });
                }
                string? remoteTemp = await engine.DownloadRemoteToTempAsync(conflict.RelativePath);
                if (remoteTemp != null && File.Exists(remoteTemp))
                {
                    Process.Start(new ProcessStartInfo(remoteTemp) { UseShellExecute = true });
                }
                else
                {
                    MessageBox.Show("无法下载云盘版本用于对比，请检查网络后重试。", "CloudPan",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
            catch (Exception ex)
            {
                window.AddLog($"对比打开失败: {ex.Message}");
            }
        }
        btnCompare.Click += OnCompareVersionsClick;
        optionPanel.Controls.Add(btnCompare, 0, 3);
        optionPanel.Controls.Add(BuildOptionDesc("同时打开本机与云盘两个版本查看后再决定"), 1, 3);

        layout.Controls.Add(optionPanel, 0, 4);

        // 安全默认：默认「保留两者」（不丢任何内容），回车即按推荐项解决
        dialog.AcceptButton = btnBoth;
        btnBoth.Focus();

        dialog.Controls.Add(layout);
        dialog.Show(window); // 非模态：冲突处理路径不再同步弹模态对话框（F-36/T-036）
    }

    /// <summary>构建版本信息面板（左侧色条 + 文字）。</summary>
    private static Panel BuildVersionPanel(Color borderColor, Color backColor, string text)
    {
        Panel panel = new Panel
        {
            Height = 28,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 2, 0, 2),
            Padding = new Padding(8, 0, 0, 0),
            BackColor = backColor,
        };
        void OnPanelPaint(object? s, PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using Pen pen = new Pen(borderColor, 3);
            e.Graphics.DrawLine(pen, 1, 2, 1, panel.Height - 4);
        }
        panel.Paint += OnPanelPaint;
        panel.Controls.Add(new Label
        {
            Text = text,
            AutoSize = true,
            Font = new Font((SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont).FontFamily, 9f),
            Location = new Point(10, 5),
        });
        return panel;
    }

    /// <summary>构建选项解释文字标签（可换行）。</summary>
    private static Label BuildOptionDesc(string text) => new Label
    {
        Text = text,
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleLeft,
        AutoSize = false,
        Margin = new Padding(8, 0, 0, 0),
        Font = new Font((SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont).FontFamily, 9f),
    };
}
