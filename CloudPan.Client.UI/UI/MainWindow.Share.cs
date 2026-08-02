using System.Diagnostics;
using System.Drawing.Drawing2D;
using CloudPan.Client.Core.Models;
using CloudPan.Client.Core.Services;
using CloudPan.Contract;
using CloudPan.Infrastructure.Design;

namespace CloudPan.Client.UI;

/// <summary>MainWindow 部分类：文件分享对话框与入口。</summary>
public partial class MainWindow
{

    // ================================================================
    // 分享 + 版本历史（T-018：文件浏览详情入口；托盘经 OpenXxxForSelection 复用）
    // ================================================================

    /// <summary>T-018：文件浏览「分享」→ 打开分享对话框（≤3 步：选文件 → 密码/过期 → 生成并复制链接）。</summary>
    private void FileBrowser_ShareRequested(FileBrowseItem item)
    {
        try
        {
            ShowShareDialog(item);
        }
        catch (Exception ex)
        {
            AddLog($"打开分享对话框失败: {ex.Message}");
        }
    }

    /// <summary>
    /// T-018：分享对话框。服务端仅提供创建/撤销端点（无列表端点），故「管理分享」= 生成后直接撤销该链接。
    /// 过期时间以 ISO 8601 UTC（"O" 格式 Z 后缀）发送，与服务端 TryParse(InvariantCulture, AdjustToUniversal) 对齐。
    /// </summary>
    private void ShowShareDialog(FileBrowseItem item)
    {
        var baseFont = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;

        Form dialog = new Form
        {
            Text = "分享文件",
            Size = new Size(580, 400),
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            ShowInTaskbar = false,
        };

        TableLayoutPanel root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16, 12, 16, 12),
            BackColor = CloudPanColors.BackgroundWhite,
        };
        root.ColumnCount = 1;
        root.RowCount = 7;
        for (int i = 0; i < 7; i++)
        {
            root.RowStyles.Add(new RowStyle(i == 6 ? SizeType.Percent : SizeType.Absolute, i == 6 ? 100 : 40));
        }

        // ① 文件路径（浏览选中，只读展示）
        Label pathLabel = new Label
        {
            Text = $"分享文件：{item.Path}",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font(baseFont.FontFamily, CloudPanFonts.SizeBody, FontStyle.Bold),
            ForeColor = CloudPanColors.TextPrimary,
        };
        root.Controls.Add(pathLabel, 0, 0);

        // ② 访问密码（可选）
        TextBox passwordBox = new TextBox
        {
            PlaceholderText = "访问密码（留空表示无需密码）",
            Dock = DockStyle.Fill,
            Font = new Font(baseFont.FontFamily, CloudPanFonts.SizeBody),
            UseSystemPasswordChar = true,
        };
        root.Controls.Add(passwordBox, 0, 1);

        // ② 过期时间
        FlowLayoutPanel expiryRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(0, 4, 0, 0),
        };
        Label expiryLabel = new Label
        {
            Text = "过期时间：",
            AutoSize = true,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font(baseFont.FontFamily, CloudPanFonts.SizeBody),
            ForeColor = CloudPanColors.TextPrimary,
        };
        ComboBox expiryCombo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 240,
            Font = new Font(baseFont.FontFamily, CloudPanFonts.SizeBody),
        };
        expiryCombo.Items.AddRange(new object[] { "永不过期", "1 天后过期", "7 天后过期", "30 天后过期" });
        expiryCombo.SelectedIndex = 0;
        expiryRow.Controls.Add(expiryLabel);
        expiryRow.Controls.Add(expiryCombo);
        root.Controls.Add(expiryRow, 0, 2);

        // ③ 生成按钮
        FlowLayoutPanel genRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            Margin = new Padding(0, 6, 0, 0),
        };
        Button genBtn = new Button
        {
            Text = "生成分享链接",
            Width = 140,
            Height = CloudPanSpacing.MinTouchSize,
            FlatStyle = FlatStyle.Flat,
            UseVisualStyleBackColor = false,
            BackColor = CloudPanColors.AccentBlue,
            ForeColor = CloudPanColors.TextOnPrimary,
        };
        genBtn.FlatAppearance.BorderColor = CloudPanColors.AccentBlue;
        genRow.Controls.Add(genBtn);
        root.Controls.Add(genRow, 0, 3);

        // 结果区（生成后显示）：链接 + 复制 + 撤销
        FlowLayoutPanel resultPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Visible = false,
            Margin = new Padding(0, 8, 0, 0),
        };
        TextBox urlBox = new TextBox
        {
            ReadOnly = true,
            Width = 520,
            Font = new Font(CloudPanFonts.FontFamilyMono, CloudPanFonts.SizeCaption),
        };
        FlowLayoutPanel resultBtns = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = true,
        };
        Button copyBtn = new Button
        {
            Text = "复制链接",
            Width = 100,
            Height = CloudPanSpacing.MinTouchSize,
            FlatStyle = FlatStyle.Flat,
            Margin = new Padding(0, 4, 8, 0),
        };
        copyBtn.FlatAppearance.BorderColor = CloudPanColors.ButtonBorderGray;
        Button revokeBtn = new Button
        {
            Text = "撤销分享",
            Width = 100,
            Height = CloudPanSpacing.MinTouchSize,
            FlatStyle = FlatStyle.Flat,
            Margin = new Padding(0, 4, 0, 0),
            UseVisualStyleBackColor = false,
            BackColor = CloudPanColors.ErrorBgLight,
            ForeColor = CloudPanColors.TextError,
        };
        revokeBtn.FlatAppearance.BorderColor = CloudPanColors.ErrorRed;
        resultBtns.Controls.Add(copyBtn);
        resultBtns.Controls.Add(revokeBtn);
        resultPanel.Controls.Add(urlBox);
        resultPanel.Controls.Add(resultBtns);
        root.Controls.Add(resultPanel, 0, 4);

        // 关闭按钮行
        FlowLayoutPanel closeRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
        };
        Button closeBtn = new Button { Text = "关闭", Width = 88, Height = CloudPanSpacing.MinTouchSize, FlatStyle = FlatStyle.Flat };
        closeBtn.FlatAppearance.BorderColor = CloudPanColors.ButtonBorderGray;
        void OnCloseClick(object? s, EventArgs e) => dialog.Close();
        closeBtn.Click += OnCloseClick;
        closeRow.Controls.Add(closeBtn);
        root.Controls.Add(closeRow, 0, 5);

        // 状态提示行
        Label statusLabel = new Label
        {
            Dock = DockStyle.Fill,
            Text = "",
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font(baseFont.FontFamily, CloudPanFonts.SizeBody),
            ForeColor = CloudPanColors.TextMuted,
        };
        root.Controls.Add(statusLabel, 0, 6);

        string? shareId = null; // 最近生成的分享 ID（供撤销）

        async void OnGenerateClick(object? s, EventArgs e)
        {
            genBtn.Enabled = false;
            try
            {
                statusLabel.Text = "正在生成…";
                string? expiresAt = expiryCombo.SelectedIndex switch
                {
                    1 => DateTime.UtcNow.AddDays(1).ToString("O"),
                    2 => DateTime.UtcNow.AddDays(7).ToString("O"),
                    3 => DateTime.UtcNow.AddDays(30).ToString("O"),
                    _ => null
                };
                string password = passwordBox.Text.Trim();
                var result = await _engine.CreateShareAsync(
                    item.Path, string.IsNullOrEmpty(password) ? null : password, expiresAt, null);
                if (result?.Data == null)
                {
                    statusLabel.Text = "生成失败，请检查服务端连接后重试";
                    return;
                }

                shareId = result.Data.ShareId;
                urlBox.Text = result.Data.Url;
                resultPanel.Visible = true;
                urlBox.Focus();
                urlBox.SelectAll();
                statusLabel.Text = result.Data.ExpiresAt != null
                    ? $"已生成（过期 {result.Data.ExpiresAt}），点击「复制链接」发送给家人"
                    : "已生成，点击「复制链接」发送给家人";
                AddLog($"已创建分享链接: {item.Path}");
            }
            catch (Exception ex)
            {
                statusLabel.Text = $"生成失败: {ex.Message}";
            }
            finally
            {
                genBtn.Enabled = true;
            }
        }
        genBtn.Click += OnGenerateClick;

        void OnCopyClick(object? s, EventArgs e)
        {
            try
            {
                Clipboard.SetText(urlBox.Text);
                statusLabel.Text = "链接已复制到剪贴板";
            }
            catch (Exception ex)
            {
                statusLabel.Text = $"复制失败: {ex.Message}";
            }
        }
        copyBtn.Click += OnCopyClick;

        async void OnRevokeClick(object? s, EventArgs e)
        {
            if (shareId == null)
            {
                return;
            }

            try
            {
                if (MessageBox.Show(dialog, "确定要撤销此分享链接吗？撤销后链接立即失效。", "撤销分享",
                        MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK)
                {
                    return;
                }

                bool ok = await _engine.RevokeShareAsync(shareId);
                statusLabel.Text = ok ? "已撤销分享，链接已失效" : "撤销失败（分享可能已失效）";
                AddLog(ok ? $"已撤销分享: {item.Path}" : $"撤销分享失败: {item.Path}");
                if (ok)
                {
                    shareId = null;
                    resultPanel.Visible = false;
                }
            }
            catch (Exception ex)
            {
                statusLabel.Text = $"撤销失败: {ex.Message}";
            }
        }
        revokeBtn.Click += OnRevokeClick;

        dialog.Controls.Add(root);
        dialog.ShowDialog(this);
    }

    // ================================================================
    // 托盘分享入口
    // ================================================================

    /// <summary>T-018：托盘「分享当前文件」入口——显示窗口并对当前选中文件打开分享对话框。</summary>
    public void OpenShareForSelection()
    {
        var item = _fileBrowser.SelectedItem;
        if (item == null || item.IsDirectory)
        {
            AddLog("请先在文件浏览中选中一个文件，再使用分享功能");
            return;
        }

        ShowShareDialog(item);
    }
}
