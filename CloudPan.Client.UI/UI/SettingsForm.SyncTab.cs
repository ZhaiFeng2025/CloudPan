namespace CloudPan.Client.UI;

/// <summary>SettingsForm 部分类：选择性同步 Tab 页。</summary>
public partial class SettingsForm
{
    // ──────────────────────────────────────────────
    // Tab 3: 选择性同步
    // ──────────────────────────────────────────────

    private void BuildSyncTab(List<string>? selectedPaths)
    {
        TabPage syncTab = new TabPage("选择性同步");
        _syncPanel = new SelectiveSyncPanel();
        if (selectedPaths != null) _syncPanel.SelectedPaths = selectedPaths;
        syncTab.Controls.Add(_syncPanel);
        _tabs.TabPages.Add(syncTab);

        // T-074：异步加载目录树填充勾选树；失败/为空时禁用保存并提示，避免空树覆盖既有排除配置
        if (_directoryTreeLoader != null)
        {
            _syncPanel.SetLoading();
            _ = LoadSyncTreeAsync();
        }
    }

    private async Task LoadSyncTreeAsync()
    {
        List<string> dirs;
        try { dirs = await _directoryTreeLoader!() ?? new List<string>(); }
        catch (Exception ex)
        {
            _syncPanel.SetLoadFailed($"目录列表加载失败：{ex.Message}\n保存将不会修改当前的排除设置。");
            return;
        }
        if (dirs.Count == 0)
        {
            _syncPanel.SetLoadFailed("服务端暂无目录列表。\n保存将不会修改当前的排除设置。");
            return;
        }
        _syncPanel.LoadFromPaths(dirs);
    }
}
