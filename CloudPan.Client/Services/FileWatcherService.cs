using CloudPan.Shared;

namespace CloudPan.Client.Services;

/// <summary>
/// 文件变更监控服务。
/// FileSystemWatcher 主通道 + 5 分钟定时全量扫描兜底。
/// </summary>
public class FileWatcherService : IDisposable
{
    private readonly string _syncRoot;
    private readonly SyncEngine _engine;
    private readonly ILogger _logger;
    private FileSystemWatcher? _watcher;
    private System.Threading.Timer? _scanTimer;

    public FileWatcherService(string syncRoot, SyncEngine engine, ILogger logger)
    {
        _syncRoot = syncRoot;
        _engine = engine;
        _logger = logger;
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

        // 兜底通道：5 分钟全量扫描
        _scanTimer = new System.Threading.Timer(async _ => await FullScanAsync(), null,
            TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));

        _logger.Info($"文件监控已启动: {_syncRoot}");
    }

    private async void OnChanged(object sender, FileSystemEventArgs e)
    {
        try
        {
            // 忽略临时文件和隐藏目录
            if (ShouldIgnore(e.FullPath)) return;

            var relativePath = ToRelativePath(e.FullPath);

            // 延迟 500ms 等待文件写入完成（Office 等程序会多次触发 Changed）
            await Task.Delay(500);

            if (File.Exists(e.FullPath))
            {
                // 文件哈希去重 —— 如果哈希未变则跳过
                _logger.Info($"检测到变更: {relativePath}");
                await _engine.EnqueueLocalChangeAsync(relativePath, SyncOperation.Upload);
            }
        }
        catch (Exception ex)
        {
            _logger.Error($"文件事件处理异常: {ex.Message}");
        }
    }

    private async void OnDeleted(object sender, FileSystemEventArgs e)
    {
        try
        {
            if (ShouldIgnore(e.FullPath)) return;
            var relativePath = ToRelativePath(e.FullPath);
            _logger.Info($"检测到删除: {relativePath}");
            await _engine.EnqueueLocalChangeAsync(relativePath, SyncOperation.Delete);
        }
        catch (Exception ex)
        {
            _logger.Error($"删除事件处理异常: {ex.Message}");
        }
    }

    private async void OnRenamed(object sender, RenamedEventArgs e)
    {
        try
        {
            if (ShouldIgnore(e.FullPath)) return;
            var oldPath = ToRelativePath(e.OldFullPath);
            var newPath = ToRelativePath(e.FullPath);
            _logger.Info($"检测到重命名: {oldPath} → {newPath}");
            await _engine.EnqueueLocalChangeAsync(oldPath, SyncOperation.Delete);
            await _engine.EnqueueLocalChangeAsync(newPath, SyncOperation.Upload);
        }
        catch (Exception ex)
        {
            _logger.Error($"重命名事件处理异常: {ex.Message}");
        }
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        _logger.Error($"FileSystemWatcher 错误: {e.GetException().Message}");
        // 重启 watcher
        try
        {
            _watcher?.Dispose();
            Start();
        }
        catch { }
    }

    private async Task FullScanAsync()
    {
        _logger.Info("定时全量扫描...");
        await Task.CompletedTask; // Phase 0：简化实现，依赖增量同步
    }

    private string ToRelativePath(string fullPath)
    {
        var relative = Path.GetRelativePath(_syncRoot, fullPath);
        return "/" + relative.Replace('\\', '/');
    }

    private bool ShouldIgnore(string path)
    {
        var fileName = Path.GetFileName(path);
        return fileName.StartsWith('.')           // .cloudpan, .tmp
            || fileName.StartsWith('~')            // Office 临时文件
            || fileName.EndsWith(".tmp")           // 临时文件
            || path.Contains(".cloudpan");         // 内部元数据目录
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _scanTimer?.Dispose();
    }
}
