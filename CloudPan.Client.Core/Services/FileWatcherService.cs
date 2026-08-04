using CloudPan.Client.Core.Models;
using CloudPan.Contract;
using CloudPan.Infrastructure.Storage;
using Microsoft.Extensions.Logging;

namespace CloudPan.Client.Core.Services;

/// <summary>
/// 文件变更监控服务。
/// FileSystemWatcher 主通道 + 定时全量扫描兜底（间隔见 SpecConfig.ScanIntervalMinutes）。
/// </summary>
public class FileWatcherService : IDisposable
{
    private readonly string _syncRoot;
    private readonly SyncEngine _engine;
    private readonly ILogger<FileWatcherService> _logger;
    private volatile List<System.Text.RegularExpressions.Regex> _ignorePatterns;
    private FileSystemWatcher? _watcher;
    private FileSystemWatcher? _ignoreWatcher;
    private System.Threading.Timer? _scanTimer;

    public FileWatcherService(SyncConfig config, SyncEngine engine, ILogger<FileWatcherService> logger)
    {
        _syncRoot = config.SyncRoot;
        _engine = engine;
        _logger = logger;
        _ignorePatterns = SyncIgnoreParser.LoadFromSyncRoot(_syncRoot);
        _logger.LogInformation("已加载 {Count} 条忽略规则", _ignorePatterns.Count);
    }

    /// <summary>重新加载 .syncignore 规则（文件变更时调用）。</summary>
    public void ReloadIgnorePatterns()
    {
        var newPatterns = SyncIgnoreParser.LoadFromSyncRoot(_syncRoot);
        // 原子替换引用避免并发枚举异常（事件线程在 foreach 中遍历时不被 Clear 干扰）
        _ignorePatterns = newPatterns;
        _logger.LogInformation("已重载 {Count} 条忽略规则", _ignorePatterns.Count);
    }

    public void Start()
    {
        Directory.CreateDirectory(_syncRoot);

        // 主通道：FileSystemWatcher
        _watcher = new FileSystemWatcher(_syncRoot)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
                         | NotifyFilters.LastWrite | NotifyFilters.Size,
            InternalBufferSize = 65536 // 64KB
        };

        _watcher.Created += OnChanged;
        _watcher.Changed += OnChanged;
        _watcher.Deleted += OnDeleted;
        _watcher.Renamed += OnRenamed;
        _watcher.Error += OnWatcherError;

        _watcher.EnableRaisingEvents = true;

        // .syncignore 文件监控（变更时自动重载规则）
        string ignorePath = Path.Combine(_syncRoot, ".syncignore");
        _ignoreWatcher?.Dispose(); // 防止重复 Start()（如 OnWatcherError 重启）时旧 watcher 泄漏
        _ignoreWatcher = new FileSystemWatcher(_syncRoot, ".syncignore")
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.CreationTime,
            EnableRaisingEvents = true
        };
        _ignoreWatcher.Changed += OnIgnoreFileChanged;
        _ignoreWatcher.Created += OnIgnoreFileChanged;

        // 兜底通道：定时全量扫描（间隔单源 shared-spec.json → SpecConfig.ScanIntervalMinutes；同步回调避免 async void 崩溃风险）
        var scanInterval = TimeSpan.FromMinutes(SpecConfig.ScanIntervalMinutes);
        _scanTimer = new System.Threading.Timer(_ =>
        {
            Task.Run(async () =>
            {
                try { await FullScanAsync(); }
                catch (Exception ex) { _logger.LogError(ex, "全量扫描调度异常"); }
            });
        }, null, scanInterval, scanInterval);

        _logger.LogInformation($"文件监控已启动: {_syncRoot}");
    }

    private async void OnChanged(object sender, FileSystemEventArgs e)
    {
        try
        {
            // 忽略临时文件和隐藏目录
            if (ShouldIgnore(e.FullPath))
            {
                return;
            }

            string relativePath = ToRelativePath(e.FullPath);

            // 小文件延迟 200ms 等待写入完成（Office 等程序会多次触发 Changed）；大文件不延迟
            if (!IsLargeFile(e.FullPath))
            {
                await Task.Delay(200);
            }

            if (File.Exists(e.FullPath))
            {
                _logger.LogInformation($"检测到文件变更: {relativePath}");
                await _engine.EnqueueLocalChangeAsync(relativePath, SyncOperation.Upload);
            }
            else if (Directory.Exists(e.FullPath))
            {
                _logger.LogInformation($"检测到目录创建: {relativePath}");
                await _engine.EnqueueLocalChangeAsync(relativePath, SyncOperation.Upload); // 目录通过 mkdir 同步
            }
        }
        catch (Exception ex)
        {
            _logger.LogError($"文件事件处理异常: {ex.Message}");
        }
    }

    private async void OnDeleted(object sender, FileSystemEventArgs e)
    {
        try
        {
            if (ShouldIgnore(e.FullPath))
            {
                return;
            }

            string relativePath = ToRelativePath(e.FullPath);
            _logger.LogInformation($"检测到删除: {relativePath}");
            await _engine.EnqueueLocalChangeAsync(relativePath, SyncOperation.Delete);
        }
        catch (Exception ex)
        {
            _logger.LogError($"删除事件处理异常: {ex.Message}");
        }
    }

    private async void OnRenamed(object sender, RenamedEventArgs e)
    {
        try
        {
            if (ShouldIgnore(e.FullPath))
            {
                return;
            }

            string oldPath = ToRelativePath(e.OldFullPath);
            string newPath = ToRelativePath(e.FullPath);

            // 使用 Rename 操作（服务端 Move API），避免子文件重复传输
            _logger.LogInformation("检测到重命名: {OldPath} → {NewPath}", oldPath, newPath);
            await _engine.EnqueueRenameAsync(oldPath, newPath);
        }
        catch (Exception ex)
        {
            _logger.LogError($"重命名事件处理异常: {ex.Message}");
        }
    }

    /// <summary>.syncignore 文件变更时重载规则。</summary>
    private void OnIgnoreFileChanged(object? sender, FileSystemEventArgs e)
    {
        _logger.LogInformation(".syncignore 已变更");
        ReloadIgnorePatterns();
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        // 日志和重启必须全部在 try 内——GetException() 可返回 null，NRE 会从
        // FileSystemWatcher 内部线程逃逸导致进程崩溃。
        try
        {
            string? errMsg = e.GetException()?.Message ?? "未知内部缓冲区溢出";
            _logger.LogError("FileSystemWatcher 错误: {Error}", errMsg);
            _watcher?.Dispose();
            _scanTimer?.Dispose();
            Start();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "重启文件监视器失败——同步事件将仅依赖 5 分钟全量扫描兜底");
        }
    }

    private async Task FullScanAsync()
    {
        try
        {
            await _engine.FullScanAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "全量扫描异常");
        }
    }

    private string ToRelativePath(string fullPath)
    {
        string relative = Path.GetRelativePath(_syncRoot, fullPath);
        return "/" + relative.Replace('\\', '/');
    }

    private bool ShouldIgnore(string fullPath)
    {
        // T-085：同步根外事件（reparse point/外部路径）不得进入同步逻辑——其相对路径含 ..，
        // 直接忽略而非让 SyncPath.ToLocalPath 抛异常，避免污染传输队列
        if (!IsInsideSyncRoot(fullPath))
        {
            return true;
        }

        // 快速硬编码检查（高频调用的性能优化）
        string fileName = Path.GetFileName(fullPath);
        if (fileName.StartsWith('~'))
        {
            return true;
        }

        // .syncignore 规则匹配
        string relativePath = "/" + Path.GetRelativePath(_syncRoot, fullPath).Replace('\\', '/');
        return SyncIgnoreParser.ShouldIgnore(relativePath, _ignorePatterns);
    }

    /// <summary>判断全路径是否位于同步根内（GetRelativePath 结果不以上级跳转 .. 开头）。</summary>
    private bool IsInsideSyncRoot(string fullPath)
    {
        try
        {
            string relative = Path.GetRelativePath(_syncRoot, fullPath);
            return !Path.IsPathRooted(relative)
                && relative != ".."
                && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false; // 解析失败按越界处理，忽略该事件
        }
    }

    /// <summary>判断是否为需要跳过延迟的大文件（超过 100MB）。</summary>
    private static bool IsLargeFile(string fullPath)
    {
        try
        {
            return new FileInfo(fullPath).Length > 100L * 1024 * 1024;
        }
        catch
        {
            // 文件已被删除或无法访问 → 不视为大文件，走正常延迟
            return false;
        }
    }

    public void Dispose()
    {
        // 先取消事件订阅再释放 watcher，防止释放期间事件回调触发（_watcher 持有的订阅引用同时被释放）
        if (_watcher != null)
        {
            _watcher.Created -= OnChanged;
            _watcher.Changed -= OnChanged;
            _watcher.Deleted -= OnDeleted;
            _watcher.Renamed -= OnRenamed;
            _watcher.Error -= OnWatcherError;
            _watcher.Dispose();
        }
        _ignoreWatcher?.Dispose();
        _scanTimer?.Dispose();
    }
}
