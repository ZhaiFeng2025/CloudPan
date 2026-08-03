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
        if (selectedPaths != null)
        {
            _syncPanel.SelectedPaths = selectedPaths;
        }

        syncTab.Controls.Add(_syncPanel);
        _tabs.TabPages.Add(syncTab);
    }
}
