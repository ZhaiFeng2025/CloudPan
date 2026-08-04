using CloudPan.Client.Core.Services;

namespace CloudPan.Client.UI;

/// <summary>MainWindow 部分类：版本历史入口（版本历史对话框本体 T-099 已下沉 VersionHistoryDialog）。</summary>
public partial class MainWindow
{

    // ================================================================
    // 版本历史（T-018）
    // ================================================================

    /// <summary>T-018：文件浏览「版本」→ 打开版本历史对话框（列表 + 回滚）。</summary>
    private async void FileBrowser_VersionHistoryRequested(FileBrowseItem item)
    {
        try
        {
            await VersionHistoryDialog.ShowAsync(this, _engine, item, AddLog, LoadBrowserAsync);
        }
        catch (Exception ex)
        {
            ErrorAttribution attribution = ErrorAttribution.FromException(ex);
            AddLog($"打开版本历史失败：{attribution.Message}。{attribution.NextStep}");
        }
    }

    /// <summary>T-018：托盘「版本历史」入口——显示窗口并对当前选中文件打开版本历史对话框。</summary>
    public async void OpenVersionHistoryForSelection()
    {
        try
        {
            var item = _fileBrowser.SelectedItem;
            if (item == null || item.IsDirectory)
            {
                AddLog("请先在文件浏览中选中一个文件，再查看版本历史");
                return;
            }

            await VersionHistoryDialog.ShowAsync(this, _engine, item, AddLog, LoadBrowserAsync);
        }
        catch (Exception ex)
        {
            ErrorAttribution attribution = ErrorAttribution.FromException(ex);
            AddLog($"打开版本历史失败：{attribution.Message}。{attribution.NextStep}");
        }
    }
}
