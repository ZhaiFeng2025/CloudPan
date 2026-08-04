using CloudPan.Client.Core.Services;

namespace CloudPan.Client.UI;

/// <summary>MainWindow 部分类：文件分享入口（分享对话框本体 T-099 已下沉 ShareDialog）。</summary>
public partial class MainWindow
{

    // ================================================================
    // 分享（T-018：文件浏览详情入口；托盘经 OpenShareForSelection 复用）
    // ================================================================

    /// <summary>T-018：文件浏览「分享」→ 打开分享对话框（≤3 步：选文件 → 密码/过期 → 生成并复制链接）。</summary>
    private void FileBrowser_ShareRequested(FileBrowseItem item)
    {
        try
        {
            ShareDialog.Show(this, _engine, item, AddLog);
        }
        catch (Exception ex)
        {
            ErrorAttribution attribution = ErrorAttribution.FromException(ex);
            AddLog($"打开分享对话框失败：{attribution.Message}。{attribution.NextStep}");
        }
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

        ShareDialog.Show(this, _engine, item, AddLog);
    }
}
