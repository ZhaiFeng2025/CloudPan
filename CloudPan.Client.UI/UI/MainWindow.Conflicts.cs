using System.Diagnostics;
using System.Drawing.Drawing2D;
using CloudPan.Client.Core.Models;
using CloudPan.Client.Core.Services;
using CloudPan.Contract;
using CloudPan.Infrastructure.Design;

namespace CloudPan.Client.UI;

/// <summary>MainWindow 部分类：冲突检测、冲突解决对话框与列表、格式化工具。</summary>
public partial class MainWindow
{

    // ================================================================
    // 冲突检测与解决
    // ================================================================

    private void OnConflictDetected(ConflictInfo conflict)
    {
        if (InvokeRequired)
        {
            Invoke(() => OnConflictDetected(conflict));
            return;
        }

        _conflicts.Add((conflict, DateTime.Now));
        UpdateConflictBadge();
        AddLog($"冲突: {Path.GetFileName(conflict.RelativePath)} — 本地和远程同时变更");
        ShowConflictResolution(conflict);
    }

    // ================================================================
    // 冲突管理
    // ================================================================

    public void ShowConflictWarning(string path)
    {
        if (InvokeRequired)
        {
            Invoke(() => ShowConflictWarning(path));
            return;
        }

        // 收集冲突信息
        string localPath = System.IO.Path.Combine(Program.SyncRoot, path.TrimStart('/'));
        DateTime localModified = DateTime.MinValue;
        long localSize = 0;
        try
        {
            if (File.Exists(localPath))
            {
                FileInfo fi = new FileInfo(localPath);
                localModified = fi.LastWriteTime;
                localSize = fi.Length;
            }
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"无法读取本地文件信息: {ex.Message}"); }

        // 从本地缓存快照获取最近已知的远程信息
        long? remoteSize = null;
        try
        {
            string dbPath = System.IO.Path.Combine(Program.SyncRoot, ".cloudpan", "client.db");
            if (File.Exists(dbPath))
            {
                using ClientDbContext db = new ClientDbContext(dbPath);
                var snapshot = db.RemoteSnapshots.Find(path);
                if (snapshot != null)
                {
                    remoteSize = snapshot.Size;
                }
            }
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"无法读取远程快照: {ex.Message}"); }

        ConflictInfo conflict = new ConflictInfo(
            RelativePath: path,
            LocalPath: localPath,
            LocalModifiedTime: localModified,
            RemoteModifiedTime: null,
            LocalFileSize: localSize,
            RemoteFileSize: remoteSize,
            RemoteHash: null
        );

        _conflicts.Add((conflict, DateTime.Now));
        UpdateConflictBadge();
        ShowConflictResolution(conflict);
    }

    /// <summary>显示冲突解决对话框——版本对比区域带颜色边框（本地蓝、远程绿）。</summary>
    private void ShowConflictResolution(ConflictInfo conflict)
    {
        string fileName = System.IO.Path.GetFileName(conflict.RelativePath);
        string localTime = conflict.LocalModifiedTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? "未知";
        string localSizeStr = FormatFileSize(conflict.LocalFileSize);
        string remoteTime = conflict.RemoteModifiedTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? "未知（服务端已变更）";
        string remoteSizeStr = conflict.RemoteFileSize.HasValue ? FormatFileSize(conflict.RemoteFileSize.Value) : "未知";

        Form dialog = new Form
        {
            Text = $"文件冲突 — {fileName}",
            Size = new Size(560, 300),
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
        Label titleLabel = new Label
        {
            Text = $"\"{fileName}\" 在本地和远程同时发生了变更",
            Font = new Font((SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont).FontFamily, 10f, FontStyle.Bold),
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 8),
        };
        layout.Controls.Add(titleLabel, 0, 0);

        // 本地版本（蓝色左边框 + 浅蓝背景）
        Panel localPanel = new Panel
        {
            Height = 28,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 2, 0, 2),
            Padding = new Padding(8, 0, 0, 0),
            BackColor = CloudPanColors.InfoBgLight, // AliceBlue 浅蓝
        };
        void OnLocalPanelPaint(object? s, PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using Pen pen = new Pen(CloudPanColors.AccentBlue, 3);
            e.Graphics.DrawLine(pen, 1, 2, 1, localPanel.Height - 4);
        }
        localPanel.Paint += OnLocalPanelPaint;
        localPanel.Controls.Add(new Label
        {
            Text = $"本地版本   修改时间: {localTime}    大小: {localSizeStr}",
            AutoSize = true,
            Font = new Font((SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont).FontFamily, 9f),
            Location = new Point(10, 5),
        });
        layout.Controls.Add(localPanel, 0, 1);

        // 远程版本（绿色左边框 + 浅绿背景）
        Panel remotePanel = new Panel
        {
            Height = 28,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 2, 0, 2),
            Padding = new Padding(8, 0, 0, 0),
            BackColor = CloudPanColors.SuccessBgLight, // Honeydew 浅绿
        };
        void OnRemotePanelPaint(object? s, PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using Pen pen = new Pen(CloudPanColors.SuccessGreen, 3);
            e.Graphics.DrawLine(pen, 1, 2, 1, remotePanel.Height - 4);
        }
        remotePanel.Paint += OnRemotePanelPaint;
        remotePanel.Controls.Add(new Label
        {
            Text = $"远程版本   修改时间: {remoteTime}    大小: {remoteSizeStr}",
            AutoSize = true,
            Font = new Font((SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont).FontFamily, 9f),
            Location = new Point(10, 5),
        });
        layout.Controls.Add(remotePanel, 0, 2);

        // 提示文字
        layout.Controls.Add(new Label
        {
            Text = "请选择处理方式:",
            AutoSize = true,
            Margin = new Padding(0, 8, 0, 0),
        }, 0, 3);

        // 按钮面板（LTR 顺序：保留本地 | 保留远程 | 保留两者）
        FlowLayoutPanel buttonPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(0, 8, 0, 0),
        };

        Button btnLocal = new Button
        {
            Text = "保留本地",
            Width = 100,
            Height = 30,
            FlatStyle = FlatStyle.Flat,
        };
        btnLocal.FlatAppearance.BorderColor = CloudPanColors.AccentBlue;
        void OnKeepLocalClick(object? s, EventArgs e)
        {
            dialog.Close();
            ResolveConflict(conflict, ConflictResolution.KeepLocal);
        }
        btnLocal.Click += OnKeepLocalClick;

        Button btnRemote = new Button
        {
            Text = "保留远程",
            Width = 100,
            Height = 30,
            FlatStyle = FlatStyle.Flat,
        };
        btnRemote.FlatAppearance.BorderColor = CloudPanColors.SuccessGreen;
        void OnKeepRemoteClick(object? s, EventArgs e)
        {
            dialog.Close();
            ResolveConflict(conflict, ConflictResolution.KeepRemote);
        }
        btnRemote.Click += OnKeepRemoteClick;

        Button btnBoth = new Button
        {
            Text = "保留两者",
            Width = 100,
            Height = 30,
            FlatStyle = FlatStyle.Flat,
        };
        btnBoth.FlatAppearance.BorderColor = CloudPanColors.WarningOrange;
        void OnKeepBothClick(object? s, EventArgs e)
        {
            dialog.Close();
            ResolveConflict(conflict, ConflictResolution.KeepBoth);
        }
        btnBoth.Click += OnKeepBothClick;

        buttonPanel.Controls.Add(btnLocal);
        buttonPanel.Controls.Add(btnRemote);
        buttonPanel.Controls.Add(btnBoth);

        layout.Controls.Add(buttonPanel, 0, 4);

        dialog.Controls.Add(layout);
        dialog.ShowDialog(this);
    }

    /// <summary>执行冲突解决，向 SyncEngine 发送回调，更新冲突列表。</summary>
    private void ResolveConflict(ConflictInfo conflict, ConflictResolution resolution)
    {
        _conflicts.RemoveAll(c => c.Info == conflict);
        UpdateConflictBadge();

        string fileName = System.IO.Path.GetFileName(conflict.RelativePath);
        AddLog(resolution switch
        {
            ConflictResolution.KeepLocal => $"冲突解决: 保留本地 — {fileName}",
            ConflictResolution.KeepRemote => $"冲突解决: 保留远程 — {fileName}",
            ConflictResolution.KeepBoth => $"冲突解决: 保留两者 — {fileName}",
            _ => $"冲突解决: {fileName}"
        });

        Task.Run(async () =>
        {
            try { await _engine.OnConflictResolved(conflict.RelativePath, resolution); }
            catch (Exception ex) { AddLog($"冲突解决失败: {ex.Message}"); }
        });
    }

    /// <summary>显示所有未解决冲突的列表对话框。</summary>
    private void ShowConflictList()
    {
        if (_conflicts.Count == 0)
        {
            MessageBox.Show("当前没有待解决的冲突。", "CloudPan",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Form listDialog = new Form
        {
            Text = $"未解决的冲突 ({_conflicts.Count})",
            Size = new Size(600, 400),
            StartPosition = FormStartPosition.CenterParent,
        };

        ListBox listBox = new ListBox
        {
            Dock = DockStyle.Top,
            Height = 280,
            IntegralHeight = false,
            Font = new Font(CloudPanFonts.FontFamilyMono, 9f),
        };

        for (int i = 0; i < _conflicts.Count; i++)
        {
            var (info, detectedAt) = _conflicts[i];
            string name = System.IO.Path.GetFileName(info.RelativePath);
            string localTime = (info.LocalModifiedTime ?? DateTime.MinValue).ToString("HH:mm:ss");
            string localSize = FormatFileSize(info.LocalFileSize);
            listBox.Items.Add($"[{i + 1}] {name}  本地: {localTime} / {localSize}  检测于: {detectedAt:HH:mm:ss}");
        }

        Button resolveBtn = new Button
        {
            Text = "解决选中冲突",
            Dock = DockStyle.Top,
            Height = 36,
            FlatStyle = FlatStyle.Flat,
        };
        resolveBtn.FlatAppearance.BorderColor = CloudPanColors.ButtonBorderGray;
        void OnResolveConflictClick(object? s, EventArgs e)
        {
            if (listBox.SelectedIndex >= 0 && listBox.SelectedIndex < _conflicts.Count)
            {
                ShowConflictResolution(_conflicts[listBox.SelectedIndex].Info);
                // 刷新列表
                listBox.Items.Clear();
                for (int i = 0; i < _conflicts.Count; i++)
                {
                    var (info, detectedAt) = _conflicts[i];
                    string name = System.IO.Path.GetFileName(info.RelativePath);
                    string localTime = (info.LocalModifiedTime ?? DateTime.MinValue).ToString("HH:mm:ss");
                    string localSize = FormatFileSize(info.LocalFileSize);
                    listBox.Items.Add($"[{i + 1}] {name}  本地: {localTime} / {localSize}  检测于: {detectedAt:HH:mm:ss}");
                }
                if (_conflicts.Count == 0)
                {
                    listDialog.Close();
                }
            }
        }
        resolveBtn.Click += OnResolveConflictClick;

        Button closeBtn = new Button
        {
            Text = "关闭",
            Dock = DockStyle.Top,
            Height = 36,
            FlatStyle = FlatStyle.Flat,
        };
        closeBtn.FlatAppearance.BorderColor = CloudPanColors.ButtonBorderGray;
        void OnListCloseClick(object? s, EventArgs e) => listDialog.Close();
        closeBtn.Click += OnListCloseClick;

        listDialog.Controls.Add(closeBtn);
        listDialog.Controls.Add(resolveBtn);
        listDialog.Controls.Add(listBox);
        listDialog.ShowDialog(this);
    }

    /// <summary>更新冲突按钮的可见性和计数文本。</summary>
    private void UpdateConflictBadge()
    {
        int count = _conflicts.Count;
        _conflictButton.Text = count > 0 ? $"冲突 ({count})" : "冲突";
        _conflictButton.Visible = count > 0;
    }

    // ================================================================
    // 格式化工具
    // ================================================================

    /// <summary>格式化文件大小为人类可读形式（B/KB/MB/GB）。</summary>
    private static string FormatFileSize(long bytes)
    {
        return bytes switch
        {
            < 1024 => $"{bytes} B",
            < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
            < 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024.0):F1} MB",
            _ => $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB"
        };
    }

    /// <summary>格式化数据传输速率（字节/秒 → "12.3 MB" 形式，小于 1MB 时显示 KB）。</summary>
    private static string FormatDataRate(double bytesPerSecond)
    {
        return bytesPerSecond switch
        {
            < 1024 => $"{bytesPerSecond:F0} B",
            < 1024 * 1024 => $"{bytesPerSecond / 1024.0:F1} KB",
            < 1024 * 1024 * 1024 => $"{bytesPerSecond / (1024.0 * 1024.0):F1} MB",
            _ => $"{bytesPerSecond / (1024.0 * 1024.0 * 1024.0):F2} GB"
        };
    }
}
