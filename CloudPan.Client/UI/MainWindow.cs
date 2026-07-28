namespace CloudPan.Client.UI;

/// <summary>
/// 主窗口——显示同步状态和传输队列。
/// </summary>
public class MainWindow : Form
{
    private readonly Label _statusLabel;
    private readonly Label _queueLabel;
    private readonly ListBox _logList;
    private readonly Button _pauseButton;
    private readonly Button _openFolderButton;

    private readonly Services.SyncEngine _engine;
    private bool _paused;

    public MainWindow(Services.SyncEngine engine)
    {
        _engine = engine;
        Text = "CloudPan — 文件同步";
        Size = new Size(600, 450);
        StartPosition = FormStartPosition.CenterScreen;
        Icon = SystemIcons.Application;

        // 状态栏
        var statusPanel = new Panel { Dock = DockStyle.Top, Height = 60, Padding = new Padding(12) };
        _statusLabel = new Label { Text = "连接中...", Font = new Font("Microsoft YaHei", 12, FontStyle.Bold), AutoSize = true };
        _queueLabel = new Label { Text = "", Font = new Font("Microsoft YaHei", 9), ForeColor = Color.Gray, AutoSize = true, Top = 28 };

        _pauseButton = new Button { Text = "暂停", Width = 80, Height = 28, Anchor = AnchorStyles.Top | AnchorStyles.Right };
        _pauseButton.Click += (_, _) => TogglePause();

        _openFolderButton = new Button { Text = "打开同步文件夹", Width = 120, Height = 28, Anchor = AnchorStyles.Top | AnchorStyles.Right };
        _openFolderButton.Click += (_, _) => OpenSyncFolder();

        var buttonPanel = new FlowLayoutPanel { Dock = DockStyle.Right, Width = 220, Padding = new Padding(8), FlowDirection = FlowDirection.RightToLeft };
        buttonPanel.Controls.Add(_pauseButton);
        buttonPanel.Controls.Add(_openFolderButton);

        statusPanel.Controls.Add(_statusLabel);
        statusPanel.Controls.Add(_queueLabel);
        statusPanel.Controls.Add(buttonPanel);

        // 日志列表
        _logList = new ListBox { Dock = DockStyle.Fill, Font = new Font("Consolas", 9) };
        _logList.Items.Add("CloudPan 客户端 v0.1.0");
        _logList.Items.Add("等待连接服务端...");

        Controls.Add(_logList);
        Controls.Add(statusPanel);

        // 事件绑定
        _engine.StatusChanged += (status) =>
        {
            if (InvokeRequired)
                Invoke(() => _statusLabel.Text = status);
            else
                _statusLabel.Text = status;
        };

        _engine.QueueProgressChanged += (completed, total) =>
        {
            if (InvokeRequired)
                Invoke(() => _queueLabel.Text = $"队列: {completed}/{total}");
            else
                _queueLabel.Text = $"队列: {completed}/{total}";
        };

        FormClosing += (_, _) => { Hide(); };
    }

    private void TogglePause()
    {
        _paused = !_paused;
        _engine.SetPaused(_paused);
        _pauseButton.Text = _paused ? "继续" : "暂停";
        AddLog(_paused ? "同步已暂停" : "同步已恢复");
    }

    private void OpenSyncFolder()
    {
        try
        {
            System.Diagnostics.Process.Start("explorer.exe", Program.SyncRoot);
        }
        catch { }
    }

    public void AddLog(string message)
    {
        if (InvokeRequired)
        {
            Invoke(() => _logList.Items.Add($"[{DateTime.Now:HH:mm:ss}] {message}"));
            Invoke(() => { if (_logList.Items.Count > 0) _logList.TopIndex = _logList.Items.Count - 1; });
        }
        else
        {
            _logList.Items.Add($"[{DateTime.Now:HH:mm:ss}] {message}");
            if (_logList.Items.Count > 0) _logList.TopIndex = _logList.Items.Count - 1;
        }
    }
}
