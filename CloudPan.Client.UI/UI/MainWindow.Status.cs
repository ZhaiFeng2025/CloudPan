using System.Diagnostics;
using System.Drawing.Drawing2D;
using CloudPan.Client.Core.Models;
using CloudPan.Client.Core.Services;
using CloudPan.Contract;
using CloudPan.Infrastructure.Design;

namespace CloudPan.Client.UI;

/// <summary>MainWindow 部分类：同步状态与进度更新、操作按钮行为。</summary>
public partial class MainWindow
{

    // ================================================================
    // 状态更新
    // ================================================================

    private void OnStatusChanged(string status)
    {
        if (InvokeRequired)
        {
            Invoke(() => ApplyStatus(status));
            return;
        }
        ApplyStatus(status);
    }

    /// <summary>
    /// 根据状态字符串更新顶部汇总：状态文字、指示灯颜色、上次同步时间与量化信息。
    /// </summary>
    private void ApplyStatus(string status)
    {
        _statusLabel.Text = status;

        // ── 状态→颜色映射 ──
        var color = status switch
        {
            string s when s.Contains("错误") || s.Contains("异常") || s.Contains("失败")
                => CloudPanColors.ErrorRed,
            string s when s.Contains("暂停")
                => CloudPanColors.WarningOrange,
            string s when s.Contains("连接") || s.Contains("等待")
                => CloudPanColors.TextMuted,
            string s when s.Contains("就绪") || s.Contains("运行中")
                => CloudPanColors.SuccessGreen,
            _ => CloudPanColors.AccentBlue
        };

        if (_statusDot.BackColor != color)
        {
            _statusDot.BackColor = color;
            _statusDot.Invalidate();
        }

        // ── 上次同步时间（供顶部汇总展示） ──
        if (status.Contains("就绪") || status.Contains("运行中"))
        {
            _lastSyncTime = DateTime.Now;
        }

        // ── 量化的状态文字 ──
        UpdateStatusInfoText(status);

        // ── 错误时显示重试按钮 ──
        bool hasError = status.Contains("错误") || status.Contains("异常") || status.Contains("失败");
        _retryButton.Visible = hasError;
    }

    /// <summary>判断状态是否表示正在同步中。</summary>
    private static bool IsActiveStatus(string status)
    {
        return status.Contains("同步") || status.Contains("上传") || status.Contains("下载");
    }

    /// <summary>更新顶部汇总的量化状态文字。同步中信息由 ApplyQueueProgress 通过 SyncStatus 对象设置，此处只处理空闲状态。</summary>
    private void UpdateStatusInfoText(string status)
    {
        // 同步中时保留 ApplyQueueProgress 设置的详细进度信息，不覆盖
        if (IsActiveStatus(status))
        {
            return;
        }

        if (_lastFileTotal > 0 || _lastSyncTime.HasValue)
        {
            // 空闲时显示文件计数和上次同步时间
            string fileInfo = _lastFileTotal > 0
                ? $"已同步 {_lastFileCompleted}/{_lastFileTotal} 文件"
                : "";
            string timeInfo = _lastSyncTime.HasValue
                ? $"上次同步: {_lastSyncTime.Value:HH:mm}"
                : "";
            _statusInfo.Text = string.Join(" · ", new[] { fileInfo, timeInfo }.Where(s => !string.IsNullOrEmpty(s)));
        }
        else
        {
            _statusInfo.Text = "";
        }
    }

    // ================================================================
    // 进度更新（字节级 SyncStatus）
    // ================================================================

    private void OnQueueProgressChanged(SyncStatus syncStatus)
    {
        if (InvokeRequired)
        {
            Invoke(() => ApplyQueueProgress(syncStatus));
            return;
        }
        ApplyQueueProgress(syncStatus);
    }

    /// <summary>
    /// 更新进度条（基于字节数，带百分比文字）、传输速率和状态量化信息。
    /// 取代原有的基于项数的进度跟踪。
    /// </summary>
    private void ApplyQueueProgress(SyncStatus status)
    {
        // 更新缓存的跟踪值
        _lastTotalBytes = status.TotalBytes;
        _lastBytesTransferred = status.BytesTransferred;
        _lastCurrentFile = status.CurrentFile;
        _lastFileTotal = status.TotalFiles;
        _lastFileCompleted = status.CompletedFiles;

        // ── 进度条（归一化到 0-10000 范围，避免 >2GB 溢出） ──
        const int progressMax = 10000;
        _progressBar.Maximum = progressMax;
        if (status.TotalBytes > 0)
        {
            _progressBar.Visible = true;
            double ratio = (double)status.BytesTransferred / Math.Max(status.TotalBytes, 1);
            _progressBar.Value = (int)(ratio * progressMax);
            _progressBar.PercentageText = $"{ratio * 100:F0}%";
        }
        else if (status.TotalFiles > 0)
        {
            // 无字节信息时回退到文件级进度
            _progressBar.Visible = true;
            double ratio = (double)status.CompletedFiles / Math.Max(status.TotalFiles, 1);
            _progressBar.Value = (int)(ratio * progressMax);
            _progressBar.PercentageText = $"{ratio * 100:F0}%";
        }
        else
        {
            _progressBar.Visible = false;
            _progressBar.PercentageText = "";
        }

        // ── 传输速率 ──
        if (status.SpeedBytesPerSec > 0)
        {
            _speedLabel.Text = $"{FormatDataRate(status.SpeedBytesPerSec)}/s";
        }
        else
        {
            _speedLabel.Text = "";
        }

        // ── 状态栏第二行（量化信息） ──
        if (!string.IsNullOrEmpty(status.CurrentFile))
        {
            // 字节级百分比
            string pct = status.TotalBytes > 0
                ? $"{(double)status.BytesTransferred / Math.Max(status.TotalBytes, 1) * 100:F0}%"
                : status.TotalFiles > 0
                    ? $"{(double)status.CompletedFiles / Math.Max(status.TotalFiles, 1) * 100:F0}%"
                    : "";
            // 速率可能尚未计算出来，避免显示 ", 45%" 这种前导逗号
            string ratePart = !string.IsNullOrEmpty(_speedLabel.Text)
                ? $"{_speedLabel.Text}, "
                : "";
            _statusInfo.Text = $"正在同步: {status.CurrentFile} ({ratePart}{pct})";
        }
        else if (status.TotalFiles > 0)
        {
            string timeInfo = _lastSyncTime.HasValue
                ? $"上次同步: {_lastSyncTime.Value:HH:mm}"
                : "";
            _statusInfo.Text = $"已同步 {status.CompletedFiles}/{status.TotalFiles} 文件 · {timeInfo}";
        }

    }

    // ================================================================
    // 操作
    // ================================================================

    private void RetrySync()
    {
        _engine.SetPaused(false);
        _retryButton.Visible = false;
        AddLog("手动触发重试，同步将在数秒内恢复...");
    }

    private void TogglePause()
    {
        _paused = !_paused;
        _engine.SetPaused(_paused);
        _pauseButton.Text = _paused ? "继续" : "暂停";
        _pauseButton.ForeColor = _paused ? CloudPanColors.ErrorRed : CloudPanColors.TextSecondary;
        _pauseButton.BackColor = _paused ? CloudPanColors.WarningBgLight : CloudPanColors.BackgroundLight;
        AddLog(_paused ? "同步已暂停" : "同步已恢复");
    }

    /// <summary>切换日志侧栏的展开/折叠（T-013：日志不再占主界面，默认折叠）。</summary>
    private void ToggleLogSidebar()
    {
        if (_splitter.Panel2Collapsed)
        {
            _splitter.Panel2Collapsed = false;
            _splitter.SplitterDistance = Math.Max(_splitter.Width - _logSidebarWidth, _splitter.Panel1MinSize);
            _logToggleButton.Text = "收起日志";
        }
        else
        {
            _logSidebarWidth = Math.Max(_splitter.Width - _splitter.SplitterDistance, _splitter.Panel2MinSize);
            _splitter.Panel2Collapsed = true;
            _logToggleButton.Text = "日志";
        }
    }

    private void OpenSyncFolder()
    {
        try
        {
            Process.Start("explorer.exe", Program.SyncRoot);
        }
        catch (Exception ex)
        {
            string msg = $"无法打开同步文件夹:\n{Program.SyncRoot}\n\n原因: {ex.Message}";
            MessageBox.Show(msg, "CloudPan", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
