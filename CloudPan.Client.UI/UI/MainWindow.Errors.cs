using System.Diagnostics;
using System.Drawing.Drawing2D;
using CloudPan.Client.Core.Models;
using CloudPan.Client.Core.Services;
using CloudPan.Contract;
using CloudPan.Infrastructure.Design;

namespace CloudPan.Client.UI;

/// <summary>MainWindow 部分类：同步错误收集、错误列表弹窗与重试。</summary>
public partial class MainWindow
{

    // ================================================================
    // 嵌入式错误面板（状态栏错误计数 + 弹出列表）
    // ================================================================

    private void OnErrorOccurred(string filePath, ErrorAttribution attribution, SyncOperation operation)
    {
        if (InvokeRequired)
        {
            Invoke(() => OnErrorOccurred(filePath, attribution, operation));
            return;
        }

        // 去重：同一文件同一错误不重复添加
        if (_errors.Any(e => e.FilePath == filePath && e.Attribution.Message == attribution.Message))
        {
            return;
        }

        SyncErrorInfo errorInfo = new SyncErrorInfo
        {
            FilePath = filePath,
            Attribution = attribution,
            Timestamp = DateTime.Now,
            Operation = operation
        };

        _errors.Add(errorInfo);
        UpdateErrorBadge();
        AddLog($"错误: {filePath} — {attribution.Message}");
    }

    /// <summary>更新状态栏错误计数标签的显示。</summary>
    private void UpdateErrorBadge()
    {
        int count = _errors.Count;
        if (count == 0)
        {
            _errorCountLabel.Visible = false;
            return;
        }

        _errorCountLabel.Text = $"❌ {count}";
        _errorCountLabel.Visible = true;
    }

    // ===== 具名事件处理器（CP301：避免匿名 lambda 订阅无法退订） =====

    private void ErrorCountLabel_Click(object? sender, EventArgs e) => ShowErrorPopup();

    private void OpenFolderButton_Click(object? sender, EventArgs e) => OpenSyncFolder();

    private void PauseButton_Click(object? sender, EventArgs e) => TogglePause();

    private void ConflictButton_Click(object? sender, EventArgs e) => ShowConflictList();

    private void RetryButton_Click(object? sender, EventArgs e) => RetrySync();

    private void LogToggleButton_Click(object? sender, EventArgs e) => ToggleLogSidebar();

    private void LogFilter_SelectedIndexChanged(object? sender, EventArgs e) => ApplyLogFilter();

    /// <summary>点击错误计数标签时弹出错误列表对话框。</summary>
    private void ShowErrorPopup()
    {
        if (_errors.Count == 0)
        {
            return;
        }

        Form dialog = new Form
        {
            Text = $"同步错误 ({_errors.Count})",
            Size = new Size(580, 380),
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
        };

        ListBox listBox = new ListBox
        {
            Dock = DockStyle.Fill,
            IntegralHeight = false,
            Font = new Font(CloudPanFonts.FontFamilyMono, 9f),
            Padding = new Padding(8),
        };

        foreach (var err in _errors)
        {
            string fileName = Path.GetFileName(err.FilePath);
            // F-31：显示白话归因 + 下一步，而非原始异常字符串
            string nextStep = string.IsNullOrEmpty(err.Attribution.NextStep) ? "" : $"（下一步：{err.Attribution.NextStep}）";
            listBox.Items.Add($"[{err.Timestamp:HH:mm:ss}] {fileName} — {err.Attribution.Message} {nextStep}");
        }

        // 右键菜单：单条重试/忽略（本地函数捕获局部状态，同时满足 CP301 具名订阅）
        ContextMenuStrip errorCms = new ContextMenuStrip();
        async void OnRetryItemClick(object? s, EventArgs e)
        {
            int idx = listBox.SelectedIndex;
            if (idx >= 0 && idx < _errors.Count)
            {
                var err = _errors[idx];
                await RetrySingleErrorAsync(err);
                listBox.Items.RemoveAt(idx);
                UpdateErrorBadge();
                if (_errors.Count == 0)
                {
                    dialog.Close();
                }
            }
        }
        void OnIgnoreItemClick(object? s, EventArgs e)
        {
            int idx = listBox.SelectedIndex;
            if (idx >= 0 && idx < _errors.Count)
            {
                _errors.RemoveAt(idx);
                listBox.Items.RemoveAt(idx);
                UpdateErrorBadge();
                if (_errors.Count == 0)
                {
                    dialog.Close();
                }
            }
        }
        errorCms.Items.Add("重试该项", null, OnRetryItemClick);
        errorCms.Items.Add("忽略该项", null, OnIgnoreItemClick);
        void OnListBoxMouseDown(object? s, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right)
            {
                int idx = listBox.IndexFromPoint(e.Location);
                if (idx >= 0)
                {
                    listBox.SelectedIndex = idx;
                    errorCms.Show(listBox, e.Location);
                }
            }
        }
        listBox.MouseDown += OnListBoxMouseDown;

        // 底部按钮栏
        FlowLayoutPanel btnPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 40,
            Padding = new Padding(8),
        };

        Button closeBtn = new Button { Text = "关闭", Width = 80, Height = 28, FlatStyle = FlatStyle.Flat };
        closeBtn.FlatAppearance.BorderColor = CloudPanColors.ButtonBorderGray;
        void OnCloseBtnClick(object? s, EventArgs e) => dialog.Close();
        closeBtn.Click += OnCloseBtnClick;

        Button retryAllBtn = new Button
        {
            Text = "全部重试",
            Width = 100,
            Height = 28,
            FlatStyle = FlatStyle.Flat,
            Margin = new Padding(0, 0, 8, 0),
            UseVisualStyleBackColor = false,
            BackColor = CloudPanColors.ErrorBgLight,
        };
        retryAllBtn.FlatAppearance.BorderColor = CloudPanColors.ErrorRed;
        async void OnRetryAllClick(object? s, EventArgs e)
        {
            await RetryAllErrorsAsync();
            dialog.Close();
        }
        retryAllBtn.Click += OnRetryAllClick;

        Button dismissAllBtn = new Button
        {
            Text = "忽略全部",
            Width = 80,
            Height = 28,
            FlatStyle = FlatStyle.Flat,
            Margin = new Padding(0, 0, 4, 0),
        };
        dismissAllBtn.FlatAppearance.BorderColor = CloudPanColors.ButtonBorderGray;
        void OnDismissAllClick(object? s, EventArgs e)
        {
            _errors.Clear();
            UpdateErrorBadge();
            dialog.Close();
        }
        dismissAllBtn.Click += OnDismissAllClick;

        btnPanel.Controls.Add(closeBtn);
        btnPanel.Controls.Add(retryAllBtn);
        btnPanel.Controls.Add(dismissAllBtn);

        dialog.Controls.Add(listBox);
        dialog.Controls.Add(btnPanel);
        dialog.ShowDialog(this);
    }

    /// <summary>异步重试所有错误条目。</summary>
    private async Task RetryAllErrorsAsync()
    {
        List<SyncErrorInfo> copy = _errors.ToList();
        foreach (var err in copy)
        {
            try
            {
                switch (err.Operation)
                {
                    case SyncOperation.Upload:
                        await _engine.EnqueueLocalChangeAsync(err.FilePath, SyncOperation.Upload);
                        break;
                    case SyncOperation.Download:
                        await _engine.DownloadPathAsync(err.FilePath);
                        break;
                    case SyncOperation.Delete:
                        await _engine.EnqueueLocalChangeAsync(err.FilePath, SyncOperation.Delete);
                        break;
                    case SyncOperation.Rename:
                        await _engine.EnqueueLocalChangeAsync(err.FilePath, SyncOperation.Rename);
                        break;
                }
                _errors.Remove(err);
            }
            catch (Exception ex)
            {
                AddLog($"重试失败: {err.FilePath} — {ex.Message}");
            }
        }
        UpdateErrorBadge();
    }

    /// <summary>异步重试单个错误条目。</summary>
    private async Task RetrySingleErrorAsync(SyncErrorInfo err)
    {
        try
        {
            switch (err.Operation)
            {
                case SyncOperation.Upload:
                    await _engine.EnqueueLocalChangeAsync(err.FilePath, SyncOperation.Upload);
                    break;
                case SyncOperation.Download:
                    await _engine.DownloadPathAsync(err.FilePath);
                    break;
                case SyncOperation.Delete:
                    await _engine.EnqueueLocalChangeAsync(err.FilePath, SyncOperation.Delete);
                    break;
                case SyncOperation.Rename:
                    await _engine.EnqueueLocalChangeAsync(err.FilePath, SyncOperation.Rename);
                    break;
            }
            _errors.Remove(err);
            AddLog($"重试成功: {err.FilePath}");
        }
        catch (Exception ex)
        {
            AddLog($"重试失败: {err.FilePath} — {ex.Message}");
        }
    }

    /// <summary>忽略单个错误条目——仅从错误列表中移除。</summary>
    private void DismissError(SyncErrorInfo error)
    {
        _errors.Remove(error);
        UpdateErrorBadge();
        AddLog($"已忽略错误: {error.FilePath}");
    }
}
