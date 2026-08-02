using System.Diagnostics;
using System.Drawing.Drawing2D;
using CloudPan.Client.Core.Models;
using CloudPan.Client.Core.Services;
using CloudPan.Contract;
using CloudPan.Infrastructure.Design;

namespace CloudPan.Client.UI;

/// <summary>MainWindow 部分类：文件浏览主视图导航与打开。</summary>
public partial class MainWindow
{

    // ================================================================
    // 文件浏览主视图（T-013）
    // ================================================================

    /// <summary>窗口首次显示/再次显示时启动文件浏览定时刷新并立即加载一次。</summary>
    private void OnShown(object? sender, EventArgs e)
    {
        _browserRefreshTimer.Start();
        BrowserRefreshTimer_Tick(sender, e);
    }

    /// <summary>定时刷新文件浏览（UI 定时器回调，async void + 顶层 try-catch 符合 CLAUDE.md 7.2）。</summary>
    private async void BrowserRefreshTimer_Tick(object? sender, EventArgs e)
    {
        if (_browserRefreshBusy)
        {
            return; // 上一次刷新仍在进行，跳过本次定时触发（防重入）
        }

        _browserRefreshBusy = true;
        try
        {
            await LoadBrowserAsync();
        }
        catch (Exception ex)
        {
            // 刷新失败不影响主界面，下次定时器触发自动重试
            System.Diagnostics.Debug.WriteLine($"刷新文件浏览失败: {ex.Message}");
        }
        finally
        {
            _browserRefreshBusy = false;
        }
    }

    /// <summary>搜索防抖定时器 Tick：停止输入后重载当前浏览目录（保持搜索关键字）。UI 定时器回调，async void + 顶层 try-catch 符合 CLAUDE.md 7.2。</summary>
    private async void SearchDebounceTimer_Tick(object? sender, EventArgs e)
    {
        _searchDebounceTimer.Stop();
        try
        {
            await LoadBrowserAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"搜索刷新失败: {ex.Message}");
        }
    }

    /// <summary>从 SyncEngine 查询文件浏览数据并渲染（数据查询在 Client.Core，UI 只渲染）。</summary>
    private async Task LoadBrowserAsync()
    {
        IReadOnlyList<FileBrowseItem> items = await _engine.GetFileBrowserAsync(_currentPath, _searchText);
        if (InvokeRequired)
        {
            Invoke(() => ApplyBrowser(items));
            return;
        }
        ApplyBrowser(items);
    }

    /// <summary>将文件浏览数据交给文件浏览视图渲染。</summary>
    private void ApplyBrowser(IReadOnlyList<FileBrowseItem> items)
    {
        _fileBrowser.ShowItems(_currentPath, items, _searchText);
    }

    /// <summary>导航到指定目录（清空搜索时经 SearchTextChanged 重载，否则直接重载）。UI 事件上下文，async void + 顶层 try-catch 符合 CLAUDE.md 7.2。</summary>
    private async void NavigateTo(string path)
    {
        _currentPath = path;
        try
        {
            if (!string.IsNullOrEmpty(_searchText))
            {
                _fileBrowser.ClearSearch(); // 触发 SearchTextChanged → 以新路径、空搜索重载
            }
            else
            {
                await LoadBrowserAsync();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"导航到 {path} 失败: {ex.Message}");
        }
    }

    /// <summary>双击子目录 / 点击面包屑段：进入目录。</summary>
    private void FileBrowser_DirectoryActivated(string path) => NavigateTo(path);

    /// <summary>双击文件：尝试用系统默认程序打开本地副本。</summary>
    private void FileBrowser_FileActivated(string path) => OpenFile(path);

    /// <summary>点击「上一级」：进入父目录。</summary>
    private void FileBrowser_UpRequested() => NavigateTo(GetParentPath(_currentPath));

    // ================================================================
    // 文件浏览导航辅助（搜索 / 路径 / 打开 / 状态映射）
    // ================================================================

    /// <summary>搜索框内容变化：记录搜索关键字并启动防抖重载。</summary>
    private void FileBrowser_SearchTextChanged(string text)
    {
        _searchText = string.IsNullOrWhiteSpace(text) ? null : text;
        _searchDebounceTimer.Stop();
        _searchDebounceTimer.Start();
    }

    /// <summary>计算目录的父目录路径（"/" 的父目录为 "/"）。</summary>
    private static string GetParentPath(string path)
    {
        string p = path.TrimEnd('/');
        if (p.Length == 0)
        {
            return "/";
        }

        int idx = p.LastIndexOf('/');
        return idx <= 0 ? "/" : p[..idx];
    }

    /// <summary>打开文件浏览视图中的文件：本地存在则系统打开，CloudOnly 未下载则提示。</summary>
    private void OpenFile(string relativePath)
    {
        string localPath = System.IO.Path.Combine(
            Program.SyncRoot, relativePath.TrimStart('/').Replace('/', System.IO.Path.DirectorySeparatorChar));
        if (System.IO.File.Exists(localPath))
        {
            try
            {
                Process.Start(new ProcessStartInfo(localPath) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                AddLog($"打开文件失败: {relativePath} — {ex.Message}");
            }
        }
        else
        {
            AddLog($"该文件仅在云端（CloudOnly），未下载到本地，暂无法打开: {relativePath}");
        }
    }

    /// <summary>将 FileBrowseItem 映射为（图标, 颜色）双通道。错误/冲突覆盖优先级最高（瞬时状态优先可见），其余按 FileState 枚举。</summary>
    private (string Icon, Color Color) ResolveBrowseState(FileBrowseItem item)
    {
        if (_errors.Any(e => string.Equals(e.FilePath, item.Path, StringComparison.OrdinalIgnoreCase)))
        {
            return ("✗", CloudPanColors.ErrorRed);
        }

        if (_conflicts.Any(c => string.Equals(c.Info.RelativePath, item.Path, StringComparison.OrdinalIgnoreCase)))
        {
            return ("!", CloudPanColors.WarningOrange);
        }

        return item.State switch
        {
            (int)FileState.Synced => ("✓", CloudPanColors.SuccessGreen),
            (int)FileState.Uploading => ("↻", CloudPanColors.AccentBlue),
            (int)FileState.Downloading => ("↻", CloudPanColors.AccentBlue),
            (int)FileState.Modified => ("↻", CloudPanColors.AccentBlue),
            (int)FileState.CloudOnly => ("☁", CloudPanColors.TextMuted),
            (int)FileState.Conflict => ("!", CloudPanColors.WarningOrange),
            _ => ("✓", CloudPanColors.SuccessGreen)
        };
    }
}
