namespace CloudPan.Server.UI;

/// <summary>管理窗口日志汇流协作类（T-110）：线程安全日志追加（句柄未建缓存、BeginInvoke 规避死锁）与缓存刷入。逻辑从 ServerWindow 外提。</summary>
internal sealed class ServerLogSink
{
    private readonly ServerWindow _form;

    public ServerLogSink(ServerWindow form)
    {
        _form = form;
    }

    /// <summary>
    /// 追加日志（线程安全）。窗口句柄创建前调用时缓存消息，句柄创建后自动刷入。
    /// 使用 BeginInvoke 避免死锁和窗口已释放异常。
    /// </summary>
    internal void Append(string msg)
    {
        if (_form.IsDisposed) return;

        // 窗口句柄尚未创建 → 缓存消息
        if (!_form.IsHandleCreated)
        {
            _form._pendingLogs.Add(msg);
            return;
        }

        if (_form.InvokeRequired)
        {
            try { _form.BeginInvoke(() => Append(msg)); }
            catch (ObjectDisposedException) { /* 窗口已关闭，静默放弃 */ }
            return;
        }
        _form._logList.Items.Add($"[{DateTime.Now:HH:mm:ss}] {msg}");
        if (_form._logList.Items.Count > 500)
        {
            _form._logList.Items.RemoveAt(0);
        }

        _form._logList.TopIndex = _form._logList.Items.Count - 1;
    }

    /// <summary>
    /// 将窗口句柄创建前缓存的消息刷入日志列表。
    /// </summary>
    internal void Flush()
    {
        if (_form._pendingLogs.Count == 0) return;
        foreach (string msg in _form._pendingLogs)
        {
            _form._logList.Items.Add($"[{DateTime.Now:HH:mm:ss}] {msg}");
        }
        _form._pendingLogs.Clear();
    }
}
