namespace CloudPan.Client.UI;

/// <summary>TrayAppContext 部分类：托盘右键菜单运行时动态构建。</summary>
public partial class TrayAppContext
{
    // ===== 右键菜单（运行时动态构建） =====

    private void ShowTrayMenu()
    {
        var menu = new ContextMenuStrip();

        menu.Items.Add("显示主窗口", null, (_, _) => ShowWindow());
        menu.Items.Add("打开同步文件夹", null, (_, _) => OpenFolder());
        menu.Items.Add("打开日志目录", null, (_, _) => OpenLogDir());
        menu.Items.Add(new ToolStripSeparator());

        // 暂停/继续
        var pauseItem = new ToolStripMenuItem(_isPaused ? "继续同步" : "暂停同步");
        pauseItem.Click += PauseItem_Click;
        menu.Items.Add(pauseItem);

        menu.Items.Add("立即同步", null, async (_, _) =>
        {
            _trayIcon.Icon = _normalIcon;
            _trayIcon.Text = "CloudPan — 正在同步";
            if (_isPaused)
            {
                _isPaused = false;
                _engine.SetPaused(false);
                _trayIcon.ShowBalloonTip(3000, "CloudPan", "同步已恢复，正在重新扫描变更...", ToolTipIcon.Info);
            }
            else
            {
                _trayIcon.ShowBalloonTip(3000, "CloudPan", "正在重新扫描变更...", ToolTipIcon.Info);
            }
            try { await _engine.FullScanAsync(_cts.Token); }
            catch (OperationCanceledException) { }
        });
        menu.Items.Add(new ToolStripSeparator());

        // 查看冲突
        int conflictCount = _conflictPaths.Count;
        var conflictItem = new ToolStripMenuItem(conflictCount > 0 ? $"查看冲突 ({conflictCount})" : "查看冲突")
        {
            Enabled = conflictCount > 0
        };
        conflictItem.Click += ConflictItem_Click;
        menu.Items.Add(conflictItem);
        menu.Items.Add(new ToolStripSeparator());

        // T-018：分享 + 版本历史入口（对文件浏览当前选中文件生效；未选中时提示）
        menu.Items.Add("分享当前文件…", null, (_, _) =>
        {
            ShowWindow();
            _mainWindow.OpenShareForSelection();
        });
        menu.Items.Add("版本历史…", null, (_, _) =>
        {
            ShowWindow();
            _mainWindow.OpenVersionHistoryForSelection();
        });
        menu.Items.Add(new ToolStripSeparator());

        // 开机自启
        var autoStartItem = new ToolStripMenuItem("开机自动启动")
        {
            CheckOnClick = true,
            Checked = IsAutoStartEnabled()
        };
        autoStartItem.Click += AutoStartItem_Click;
        menu.Items.Add(autoStartItem);
        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add("设置", null, (_, _) => OpenSettings());
        menu.Items.Add("关于", null, (_, _) =>
        {
            var ver = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version;
            string verStr = ver != null ? $"{ver.Major}.{ver.Minor}.{ver.Build}" : "1.0.0";
            MessageBox.Show(
                $"CloudPan 文件同步系统\n版本 {verStr}\n\n自托管家庭文件同步\n数据完全保存在您的设备上\n\n同步目录: {Program.SyncRoot}\n服务端: {Program.ServerUrl}",
                "关于 CloudPan", MessageBoxButtons.OK, MessageBoxIcon.Information);
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => Exit());

        menu.Show(Cursor.Position);
    }
}
