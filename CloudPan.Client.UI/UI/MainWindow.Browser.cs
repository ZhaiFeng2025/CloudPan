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

    /// <summary>
    /// 定时刷新文件浏览（UI 定时器回调，async void + 顶层 try-catch 符合 CLAUDE.md 7.2）。
    /// T-108：先后台刷新 /api/tree 快照缓存并取数据版本，仅当前浏览数据变化才重查+重渲染，
    /// 消除每 5 秒对同步根的全树递归枚举 + 快照全表读取造成的周期卡顿。
    /// </summary>
    private async void BrowserRefreshTimer_Tick(object? sender, EventArgs e)
    {
        if (_browserRefreshBusy)
        {
            return; // 上一次刷新仍在进行，跳过本次定时触发（防重入）
        }

        _browserRefreshBusy = true;
        try
        {
            long version = await _engine.RefreshBrowserDataAsync();
            if (version == _lastBrowserVersion)
            {
                return; // 当前浏览数据未变化，跳过重渲染
            }

            await LoadBrowserAsync();
            _lastBrowserVersion = version;
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
    /// <remarks>T-108：先经 RefreshBrowserDataAsync 后台刷新 /api/tree 快照缓存（消除 UI 线程全表读取），再查缓存渲染。</remarks>
    private async Task LoadBrowserAsync()
    {
        await _engine.RefreshBrowserDataAsync();
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

    /// <summary>双击文件：本地存在则系统打开，CloudOnly 弹下载确认（T-033）。</summary>
    private void FileBrowser_FileActivated(FileBrowseItem item) => OpenFile(item);

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

    /// <summary>打开文件浏览视图中的文件：本地存在则系统打开，CloudOnly 弹下载确认而非仅日志（T-033）。</summary>
    private void OpenFile(FileBrowseItem item)
    {
        string localPath = System.IO.Path.Combine(
            Program.SyncRoot, item.Path.TrimStart('/').Replace('/', System.IO.Path.DirectorySeparatorChar));
        if (System.IO.File.Exists(localPath))
        {
            try
            {
                Process.Start(new ProcessStartInfo(localPath) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                // T-115：主动打开失败弹白话提示（原因+下一步），不再只写默认折叠的日志栏
                ErrorAttribution attribution = ErrorAttribution.FromException(ex);
                AddLog($"打开文件失败: {item.Path} — {ex.Message}");
                MessageBox.Show(this, $"无法打开文件：{attribution.Message}。{attribution.NextStep}", "打开文件",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            return;
        }

        if (item.State == (int)FileState.CloudOnly)
        {
            var result = MessageBox.Show(
                $"该文件仅在云端，尚未下载到本机。\n\n是否立即下载到本机？\n\n{item.Path}",
                "CloudPan — 下载文件",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);
            if (result == DialogResult.Yes)
            {
                StartDownload(item.Path);
            }
        }
        else
        {
            AddLog($"该文件仅在云端，未下载到本地，暂无法打开: {item.Path}");
        }
    }

    /// <summary>T-033：工具栏「上传」→ 复制到当前浏览目录并入队上传。</summary>
    private async void FileBrowser_UploadRequested(string[] files) => await ImportFilesAsync(files);

    /// <summary>T-033：拖拽文件到浏览视图 → 复制到当前浏览目录并入队上传。</summary>
    private async void FileBrowser_FilesDropped(string[] files) => await ImportFilesAsync(files);

    /// <summary>T-033/T-083：「下载到本机」/右键菜单 → 批量 CloudOnly 文件入队下载（每个 StartDownload 内部捕获异常，互不影响）。</summary>
    private void FileBrowser_DownloadRequested(IReadOnlyList<FileBrowseItem> items)
    {
        foreach (FileBrowseItem item in items)
        {
            StartDownload(item.Path);
        }
    }

    /// <summary>T-033：CloudOnly 按需下载：入队高优先级下载并立即刷新（文件显示 ↻ 下载中）。</summary>
    private async void StartDownload(string relativePath)
    {
        try
        {
            await _engine.DownloadPathAsync(relativePath);
            AddLog($"已开始下载到本机: {relativePath}");
            await LoadBrowserAsync();
        }
        catch (Exception ex)
        {
            // T-115：主动下载入队失败弹白话提示（原因+下一步），不再只写默认折叠的日志栏
            ErrorAttribution attribution = ErrorAttribution.FromException(ex);
            AddLog($"下载入队失败: {relativePath} — {ex.Message}");
            MessageBox.Show(this, $"下载文件失败：{attribution.Message}。{attribution.NextStep}", "下载文件",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>T-033：导入文件到当前浏览目录（复制 + 入队上传 + 立即刷新）。async void 调用方内部捕获全部异常。</summary>
    private async Task ImportFilesAsync(string[] files)
    {
        if (files.Length == 0)
        {
            return;
        }

        try
        {
            await _engine.ImportFilesAsync(files, _currentPath);
            AddLog($"已导入 {files.Length} 个文件到 {_currentPath}");
            await LoadBrowserAsync();
        }
        catch (Exception ex)
        {
            // T-115：主动导入失败弹白话提示（原因+下一步），不再只写默认折叠的日志栏
            ErrorAttribution attribution = ErrorAttribution.FromException(ex);
            AddLog($"导入文件失败: {ex.Message}");
            MessageBox.Show(this, $"导入文件失败：{attribution.Message}。{attribution.NextStep}", "导入文件",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
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
