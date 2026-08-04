using CloudPan.Client.Core.Services;
using CloudPan.Infrastructure.Design;

namespace CloudPan.Client.UI;

/// <summary>MainWindow 部分类：冲突检测、非模态聚合冲突列表与徽标（T-036）。</summary>
public partial class MainWindow
{
    // ================================================================
    // 冲突检测与解决
    // ================================================================

    // 非模态聚合冲突列表（单实例：批量冲突只弹一次，F-36/T-036）
    private Form? _conflictListForm;
    private ListBox? _conflictListBox;

    private void OnConflictDetected(ConflictInfo conflict)
    {
        if (InvokeRequired)
        {
            Invoke(() => OnConflictDetected(conflict));
            return;
        }

        AddConflict(conflict);
        AddLog($"冲突: {Path.GetFileName(conflict.RelativePath)} — 本地和远程同时变更");
        // 非模态聚合：只自动打开冲突列表，不再逐个弹模态对话框（避免批量冲突堆叠，F-36/T-036）。
        // 主窗口隐藏到托盘时不强行弹出（托盘气泡已通知，重开后经顶部冲突按钮/列表查看）。
        if (Visible)
        {
            EnsureConflictListOpen();
        }
    }

    // ================================================================
    // 冲突管理
    // ================================================================

    /// <summary>加入/更新冲突集合（按相对路径去重），刷新徽标与聚合列表。</summary>
    private void AddConflict(ConflictInfo conflict)
    {
        int idx = _conflicts.FindIndex(c => c.Info.RelativePath == conflict.RelativePath);
        if (idx >= 0)
        {
            _conflicts[idx] = (conflict, DateTime.Now);
        }
        else
        {
            _conflicts.Add((conflict, DateTime.Now));
        }
        UpdateConflictBadge();
        RefreshConflictListItems();
    }

    /// <summary>顶部冲突按钮 / 托盘菜单入口：打开聚合冲突列表（空时提示）。</summary>
    public void ShowConflictList()
    {
        if (InvokeRequired)
        {
            Invoke(() => ShowConflictList());
            return;
        }

        if (_conflicts.Count == 0)
        {
            MessageBox.Show("当前没有待解决的冲突。", "CloudPan",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        EnsureConflictListOpen();
    }

    /// <summary>打开/聚焦非模态聚合冲突列表（单实例，批量冲突只弹一次）。</summary>
    private void EnsureConflictListOpen()
    {
        if (_conflictListForm == null || _conflictListForm.IsDisposed)
        {
            _conflictListForm = new Form
            {
                Text = "未解决的冲突",
                Size = new Size(640, 420),
                StartPosition = FormStartPosition.CenterParent,
                ShowInTaskbar = false,
            };
            _conflictListBox = new ListBox
            {
                Dock = DockStyle.Top,
                Height = 300,
                IntegralHeight = false,
                Font = new Font(CloudPanFonts.FontFamilyMono, 9f),
            };

            Button resolveBtn = new Button
            {
                Text = "解决选中冲突",
                Dock = DockStyle.Top,
                Height = 36,
                FlatStyle = FlatStyle.Flat,
            };
            resolveBtn.FlatAppearance.BorderColor = CloudPanColors.ButtonBorderGray;
            resolveBtn.Click += ConflictListResolve_Click;

            Button closeBtn = new Button
            {
                Text = "关闭",
                Dock = DockStyle.Top,
                Height = 36,
                FlatStyle = FlatStyle.Flat,
            };
            closeBtn.FlatAppearance.BorderColor = CloudPanColors.ButtonBorderGray;
            closeBtn.Click += ConflictListClose_Click;

            _conflictListForm.Controls.Add(closeBtn);
            _conflictListForm.Controls.Add(resolveBtn);
            _conflictListForm.Controls.Add(_conflictListBox);
            _conflictListForm.FormClosed += ConflictList_FormClosed;
            _conflictListForm.Show(this); // 非模态，挂靠主窗
        }
        else
        {
            _conflictListForm.Activate();
        }
        RefreshConflictListItems();
    }

    /// <summary>聚合列表「解决选中冲突」：打开该冲突的非模态解决对话框。</summary>
    private void ConflictListResolve_Click(object? sender, EventArgs e)
    {
        if (_conflictListBox == null || _conflictListBox.SelectedIndex < 0)
        {
            return;
        }
        int idx = _conflictListBox.SelectedIndex;
        if (idx >= 0 && idx < _conflicts.Count)
        {
            ConflictResolutionDialog.Show(this, _engine, _conflicts[idx].Info);
        }
    }

    /// <summary>执行冲突解决，向 SyncEngine 发送回调，更新冲突列表（由 ConflictResolutionDialog 回调）。</summary>
    internal void ResolveConflict(ConflictInfo conflict, ConflictResolution resolution)
    {
        _conflicts.RemoveAll(c => c.Info == conflict);
        UpdateConflictBadge();
        RefreshConflictListItems();
        if (_conflicts.Count == 0)
        {
            _conflictListForm?.Close();
        }

        string fileName = Path.GetFileName(conflict.RelativePath);
        AddLog(resolution switch
        {
            ConflictResolution.KeepLocal => $"冲突解决: 保留本机版本 — {fileName}",
            ConflictResolution.KeepRemote => $"冲突解决: 保留服务端版本 — {fileName}",
            ConflictResolution.KeepBoth => $"冲突解决: 保留两者 — {fileName}",
            ConflictResolution.ForceDelete => $"冲突解决: 仍删除（强制） — {fileName}",
            _ => $"冲突解决: {fileName}"
        });

        Task.Run(async () =>
        {
            try { await _engine.OnConflictResolved(conflict.RelativePath, resolution); }
            catch (Exception ex) { AddLog($"冲突解决失败: {ex.Message}"); }
        });
    }

    private void ConflictListClose_Click(object? sender, EventArgs e) => _conflictListForm?.Close();

    private void ConflictList_FormClosed(object? sender, FormClosedEventArgs e)
    {
        _conflictListForm = null;
        _conflictListBox = null;
    }

    /// <summary>刷新聚合列表条目（新增/解决冲突后实时反映）。</summary>
    private void RefreshConflictListItems()
    {
        if (_conflictListBox == null)
        {
            return;
        }
        _conflictListBox.Items.Clear();
        for (int i = 0; i < _conflicts.Count; i++)
        {
            var (info, detectedAt) = _conflicts[i];
            string name = Path.GetFileName(info.RelativePath);
            string localTime = (info.LocalModifiedTime ?? DateTime.MinValue).ToString("HH:mm:ss");
            string localSize = UiFormat.FormatFileSize(info.LocalFileSize);
            string remoteTime = info.RemoteModifiedTime?.ToString("HH:mm:ss") ?? "—";
            _conflictListBox.Items.Add($"[{i + 1}] {name}  本机: {localTime} / {localSize}  云盘: {remoteTime}  检测于: {detectedAt:HH:mm:ss}");
        }
    }

    /// <summary>更新冲突按钮的可见性和计数文本。</summary>
    private void UpdateConflictBadge()
    {
        int count = _conflicts.Count;
        _conflictButton.Text = count > 0 ? $"冲突 ({count})" : "冲突";
        _conflictButton.Visible = count > 0;
    }
}
