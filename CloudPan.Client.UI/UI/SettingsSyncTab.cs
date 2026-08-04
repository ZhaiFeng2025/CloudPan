namespace CloudPan.Client.UI;

/// <summary>设置窗口选择性同步 Tab 协作类（T-109）：同步页构建与目录树异步加载填充。</summary>
internal sealed class SettingsSyncTab
{
    private readonly SettingsForm _form;

    public SettingsSyncTab(SettingsForm form)
    {
        _form = form;
    }

    // ──────────────────────────────────────────────
    // Tab 3: 选择性同步
    // ──────────────────────────────────────────────

    public void BuildSyncTab(List<string>? selectedPaths)
    {
        TabPage syncTab = new TabPage("选择性同步");
        _form._syncPanel = new SelectiveSyncPanel();
        if (selectedPaths != null) _form._syncPanel.SelectedPaths = selectedPaths;
        syncTab.Controls.Add(_form._syncPanel);
        _form._tabs.TabPages.Add(syncTab);

        // T-074：异步加载目录树填充勾选树；失败/为空时禁用保存并提示，避免空树覆盖既有排除配置
        if (_form._directoryTreeLoader != null)
        {
            _form._syncPanel.SetLoading();
            _ = LoadSyncTreeAsync();
        }
    }

    private async Task LoadSyncTreeAsync()
    {
        List<string> dirs;
        try { dirs = await _form._directoryTreeLoader!() ?? new List<string>(); }
        catch (Exception ex)
        {
            _form._syncPanel.SetLoadFailed($"目录列表加载失败：{ex.Message}\n保存将不会修改当前的排除设置。");
            return;
        }
        if (dirs.Count == 0)
        {
            _form._syncPanel.SetLoadFailed("服务端暂无目录列表。\n保存将不会修改当前的排除设置。");
            return;
        }
        _form._syncPanel.LoadFromPaths(dirs);
    }
}
