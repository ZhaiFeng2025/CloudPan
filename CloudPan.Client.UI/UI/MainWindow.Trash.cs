using CloudPan.Client.Core.Services;
using CloudPan.Contract;

namespace CloudPan.Client.UI;

/// <summary>MainWindow 部分类：删除进回收站、撤销 Snackbar 与最近删除入口（回收站对话框本体 T-099 已下沉 TrashDialog）。</summary>
public partial class MainWindow
{

    // ================================================================
    // 删除进回收站 + 撤销 + 最近删除入口（T-014）
    // ================================================================

    /// <summary>从回收站条目原始路径取显示名（最后一段）。</summary>
    private static string TrashDisplayName(TrashItem t)
    {
        string p = t.OriginalPath.TrimEnd('/');
        return p[(p.LastIndexOf('/') + 1)..];
    }

    /// <summary>T-014/T-083/T-092/T-115：文件浏览「删除」/「批量删除」→ 全部进回收站（服务端软删墓碑+移入回收站），本地副本即时删除，显示撤销 Snackbar（5 秒）。批量删除（多选）前弹确认对话框（对齐 Android AlertDialog），单个删除不弹；逐项删除遇失败时弹「成功 N / 失败 M」汇总白话提示。</summary>
    private async void FileBrowser_DeleteRequested(IReadOnlyList<FileBrowseItem> items)
    {
        try
        {
            // T-092：批量删除（多选）前弹确认，防止误触全选；单个删除不弹（对齐 Android AlertDialog 行为）
            if (items.Count > 1 &&
                MessageBox.Show(this, $"将删除 {items.Count} 项，移入回收站可恢复。", "删除确认",
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK)
            {
                return;
            }

            var trashed = new List<TrashItem>();
            int failed = 0;
            Exception? lastException = null;
            foreach (FileBrowseItem item in items)
            {
                try
                {
                    TrashItem? trashItem = await _engine.DeleteForTrashAsync(item.Path);
                    AddLog(trashItem != null ? $"已删除（可撤销）: {item.Name}" : $"已删除: {item.Name}");
                    if (trashItem != null)
                    {
                        trashed.Add(trashItem);
                    }
                }
                catch (Exception ex)
                {
                    // T-115：逐项失败记入汇总，该项本地副本保留、浏览视图刷新后仍可见可重试
                    failed++;
                    lastException = ex;
                    AddLog($"删除失败: {item.Name} — {ex.Message}");
                }
            }

            if (failed > 0)
            {
                // T-115：批量操作失败弹「成功 N / 失败 M」汇总（白话归因：原因+下一步），不再只写默认折叠的日志栏
                ErrorAttribution attribution = ErrorAttribution.FromException(lastException!);
                MessageBox.Show(this,
                    $"删除完成：成功 {items.Count - failed} 项 / 失败 {failed} 项。\n\n{attribution.Message}。{attribution.NextStep}",
                    "删除结果", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            if (trashed.Count > 0)
            {
                _lastDeletedTrashItems = trashed;
                // T-092：提示可到回收站恢复，避免用户误以为删除不可撤销
                _undoLabel.Text = trashed.Count == 1
                    ? $"已删除 “{TrashDisplayName(trashed[0])}”，可在 5 秒内撤销，也可到回收站恢复"
                    : $"已删除 {trashed.Count} 个项目，可在 5 秒内撤销，也可到回收站恢复";
                _undoBar.Visible = true;
                _undoBar.BringToFront();
                _undoTimer.Stop();
                _undoTimer.Start();
            }
            else
            {
                _undoBar.Visible = false;
            }

            await LoadBrowserAsync();
        }
        catch (Exception ex)
        {
            // T-115：兜底捕获逐项删除之外的意外异常（如删除后刷新浏览失败），防 async void 异常逃逸（CLAUDE.md 7.2）
            AddLog($"删除操作异常: {ex.Message}");
        }
    }

    /// <summary>T-014：点击「回收站」→ 打开最近删除入口（列表/恢复/清空，复用服务端三端点）。</summary>
    private async void FileBrowser_TrashRequested()
    {
        try
        {
            List<TrashItem> items = await _engine.GetTrashAsync();
            TrashDialog.Show(this, _engine, AddLog, items);
        }
        catch (Exception ex)
        {
            AddLog($"打开回收站失败: {ex.Message}");
        }
    }

    /// <summary>T-014/T-083/T-115：点击撤销 → 恢复最近删除的全部回收站条目（5 秒窗口内有效）。逐项恢复遇失败时弹「成功 N / 失败 M」汇总白话提示。</summary>
    private async void UndoButton_Click(object? sender, EventArgs e)
    {
        _undoTimer.Stop();
        _undoBar.Visible = false;
        List<TrashItem> items = _lastDeletedTrashItems;
        _lastDeletedTrashItems = new();
        if (items.Count == 0)
        {
            return;
        }

        try
        {
            int succeeded = 0;
            int failed = 0;
            Exception? lastException = null;
            foreach (TrashItem item in items)
            {
                try
                {
                    bool ok = await _engine.RestoreTrashAsync(item);
                    AddLog(ok ? $"已撤销删除，恢复文件: {item.OriginalPath}" : $"撤销失败: {item.OriginalPath}");
                    if (ok)
                    {
                        succeeded++;
                    }
                    else
                    {
                        failed++;
                    }
                }
                catch (Exception ex)
                {
                    // T-115：逐项失败记入汇总，不中断其余条目恢复
                    failed++;
                    lastException = ex;
                    AddLog($"撤销失败: {item.OriginalPath} — {ex.Message}");
                }
            }

            if (failed > 0)
            {
                // T-115：撤销失败弹「成功 N / 失败 M」汇总（白话归因：原因+下一步），不再只写默认折叠的日志栏
                string reason;
                if (lastException != null)
                {
                    ErrorAttribution attribution = ErrorAttribution.FromException(lastException);
                    reason = $"{attribution.Message}。{attribution.NextStep}";
                }
                else
                {
                    reason = "部分文件未能恢复，请检查网络连接后重试。";
                }
                MessageBox.Show(this, $"撤销完成：成功 {succeeded} 项 / 失败 {failed} 项。\n\n{reason}",
                    "撤销结果", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            else
            {
                await LoadBrowserAsync();
            }
        }
        catch (Exception ex)
        {
            // T-115：兜底捕获逐项恢复之外的意外异常（如恢复后刷新浏览失败），防 async void 异常逃逸（CLAUDE.md 7.2）
            AddLog($"撤销操作异常: {ex.Message}");
        }
    }

    /// <summary>T-014：撤销窗口超时 → 隐藏 Snackbar，丢弃撤销机会。</summary>
    private void UndoTimer_Tick(object? sender, EventArgs e)
    {
        _undoTimer.Stop();
        _undoBar.Visible = false;
        _lastDeletedTrashItems = new();
    }
}
