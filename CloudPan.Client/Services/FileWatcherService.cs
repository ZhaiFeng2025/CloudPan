using CloudPan.Client.Models;
using CloudPan.Shared;
using Microsoft.Extensions.Logging;

namespace CloudPan.Client.Services;

/// <summary>
/// 文件变更监控服务。
/// FileSystemWatcher 主通道 + 5 分钟定时全量扫描兜底。
/// </summary>
public class FileWatcherService : IDisposable
{
    private readonly string _syncRoot;
    private readonly SyncEngine _engine;
    private readonly ILogger<FileWatcherService> _logger;
    private FileSystemWatcher? _watcher;
    private System.Threading.Timer? _scanTimer;

    public FileWatcherService(SyncConfig config, SyncEngine engine, ILogger<FileWatcherService> logger)
    {
        _syncRoot = config.SyncRoot;
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

        _logger.LogInformation($"文件监控已启动: {_syncRoot}");
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
            if (ShouldIgnore(e.FullPath)) return;
            var relativePath = ToRelativePath(e.FullPath);
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
            if (ShouldIgnore(e.FullPath)) return;
            var oldPath = ToRelativePath(e.OldFullPath);
            var newPath = ToRelativePath(e.FullPath);

            if (Directory.Exists(e.FullPath))
            {
                // 目录重命名：递归枚举所有子文件，入队上传
                _logger.LogInformation($"检测到目录重命名: {oldPath} → {newPath}");
                await _engine.EnqueueLocalChangeAsync(oldPath, SyncOperation.Delete);

                foreach (var file in Directory.GetFiles(e.FullPath, "*", SearchOption.AllDirectories))
                {
                    var relPath = "/" + Path.GetRelativePath(_syncRoot, file).Replace('\\', '/');
                    await _engine.EnqueueLocalChangeAsync(relPath, SyncOperation.Upload);
                }
                // 创建新目录结构（通过上传文件时自动创建父目录）
            }
            else
            {
                _logger.LogInformation($"检测到文件重命名: {oldPath} → {newPath}");
                await _engine.EnqueueLocalChangeAsync(oldPath, SyncOperation.Delete);
                await _engine.EnqueueLocalChangeAsync(newPath, SyncOperation.Upload);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError($"重命名事件处理异常: {ex.Message}");
        }
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        _logger.LogError($"FileSystemWatcher 错误: {e.GetException().Message}");
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
