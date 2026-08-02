using CloudPan.Client.Core.Models;
using CloudPan.Client.Core.Services;
using CloudPan.Contract;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CloudPan.Tests.Client.Services;

/// <summary>
/// FileWatcherService 单元测试——验证忽略规则过滤、文件创建/修改事件入队、重命名去重。
/// 使用 TestBase 提供的临时目录；事件处理通过真实 FileSystemWatcher 或反射调用私有处理程序。
/// </summary>
public class FileWatcherServiceTests : Infrastructure.TestBase, IDisposable
{
    private readonly string _syncRoot;
    private readonly MockApiClient _api;
    private readonly IDbContextFactory<ClientDbContext> _dbFactory;
    private readonly SyncEngine _engine;
    private readonly FileWatcherService _watcher;

    private static readonly System.Reflection.BindingFlags NonPublicInstance =
        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;

    public FileWatcherServiceTests()
    {
        // 同步根使用子目录，避免测试数据库文件被 FullScan 扫描到
        _syncRoot = Path.Combine(TempDir, "sync");
        Directory.CreateDirectory(_syncRoot);

        // 测试数据库放在同步根之外
        string dbPath = Path.Combine(TempDir, "client-test.db");
        _dbFactory = new TestClientDbFactory(dbPath);
        using (var db = _dbFactory.CreateDbContext())
        {
            db.Database.EnsureCreated();
            db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
        }

        _api = new MockApiClient();
        SyncConfig config = new SyncConfig { SyncRoot = _syncRoot, ServerUrl = "http://localhost:8443" };
        _engine = new SyncEngine(_api, config, _dbFactory,
            NullLoggerFactory.Instance.CreateLogger<SyncEngine>());
        _watcher = new FileWatcherService(config, _engine,
            NullLoggerFactory.Instance.CreateLogger<FileWatcherService>());
    }

    /// <summary>
    /// 重新实现 IDisposable：基类 TestBase.Dispose 会删除临时目录，
    /// 但必须先释放 watcher/引擎（否则文件句柄会导致目录删除失败）。
    /// </summary>
    void IDisposable.Dispose()
    {
        _watcher.Dispose();
        _engine.Dispose();
        base.Dispose();
    }

    // ============================================================
    // ShouldIgnore 过滤规则（.tmp、~*、.cloudpan、**/.git/**）
    // ============================================================

    [Theory]
    [InlineData("report.tmp")]
    [InlineData("docs/report.tmp")]
    public void ShouldIgnore_临时文件_返回true(string relative)
    {
        // Arrange
        string fullPath = Path.Combine(_syncRoot, relative);

        // Act
        bool ignored = InvokeShouldIgnore(_watcher, fullPath);

        // Assert
        Assert.True(ignored);
    }

    [Theory]
    [InlineData("~$report.docx")]
    [InlineData("docs/~$presentation.pptx")]
    public void ShouldIgnore_Office临时文件_返回true(string relative)
    {
        // Arrange
        string fullPath = Path.Combine(_syncRoot, relative);

        // Act
        bool ignored = InvokeShouldIgnore(_watcher, fullPath);

        // Assert
        Assert.True(ignored);
    }

    [Fact]
    public void ShouldIgnore_元数据目录_返回true()
    {
        // Arrange：.cloudpan 目录内部文件
        string fullPath = Path.Combine(_syncRoot, ".cloudpan", "db.sqlite");

        // Act
        bool ignored = InvokeShouldIgnore(_watcher, fullPath);

        // Assert
        Assert.True(ignored);
    }

    [Fact]
    public void ShouldIgnore_Git仓库_返回true()
    {
        // Arrange：.git 目录内部文件
        string fullPath = Path.Combine(_syncRoot, "repo", ".git", "config");

        // Act
        bool ignored = InvokeShouldIgnore(_watcher, fullPath);

        // Assert
        Assert.True(ignored);
    }

    [Theory]
    [InlineData("readme.md")]
    [InlineData("docs/report.txt")]
    [InlineData("src/Program.cs")]
    public void ShouldIgnore_普通文件_返回false(string relative)
    {
        // Arrange
        string fullPath = Path.Combine(_syncRoot, relative);

        // Act
        bool ignored = InvokeShouldIgnore(_watcher, fullPath);

        // Assert
        Assert.False(ignored);
    }

    // ============================================================
    // 文件创建/修改事件
    // ============================================================

    [Fact]
    public async Task 创建文件_触发事件_入队上传()
    {
        // Arrange
        _watcher.Start();
        string filePath = Path.Combine(_syncRoot, "created.txt");

        // Act：创建文件触发 FileSystemWatcher.Created
        await File.WriteAllTextAsync(filePath, "watched");

        // Assert：队列中出现上传项
        var item = await WaitUntilAsync(async () =>
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            return await db.SyncQueue.FirstOrDefaultAsync(q => q.FilePath == "/created.txt");
        });
        Assert.NotNull(item);
        Assert.Equal((int)SyncOperation.Upload, item!.Operation);
    }

    [Fact]
    public async Task 修改文件_触发事件_入队上传()
    {
        // Arrange：文件在 watcher 启动前已存在，避免 Created 事件干扰
        string filePath = Path.Combine(_syncRoot, "modified.txt");
        await File.WriteAllTextAsync(filePath, "v1");
        _watcher.Start();

        // Act：修改内容（大小变化，绕过大小去重）
        await File.WriteAllTextAsync(filePath, "v2-with-longer-content");

        // Assert：队列中出现上传项
        var item = await WaitUntilAsync(async () =>
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            return await db.SyncQueue.FirstOrDefaultAsync(q => q.FilePath == "/modified.txt");
        });
        Assert.NotNull(item);
        Assert.Equal((int)SyncOperation.Upload, item!.Operation);
    }

    // ============================================================
    // 重命名事件 + 去重
    // ============================================================

    [Fact]
    public async Task 重命名文件_触发事件_入队重命名()
    {
        // Arrange
        string src = Path.Combine(_syncRoot, "rename-src.txt");
        await File.WriteAllTextAsync(src, "x");
        _watcher.Start();

        // Act：触发 FileSystemWatcher.Renamed
        File.Move(src, Path.Combine(_syncRoot, "rename-dst.txt"));

        // Assert：入队 Rename 操作，旧路径 → 新路径
        var item = await WaitUntilAsync(async () =>
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            return await db.SyncQueue.FirstOrDefaultAsync(q =>
                q.FilePath == "/rename-src.txt" && q.Operation == (int)SyncOperation.Rename);
        });
        Assert.NotNull(item);
        Assert.Equal("/rename-dst.txt", item!.TargetPath);
    }

    [Fact]
    public async Task 重命名_相同源路径_去重并更新目标()
    {
        // Arrange
        string src = Path.Combine(_syncRoot, "dup-src.txt");
        await File.WriteAllTextAsync(src, "x");
        _watcher.Start();

        // Act：同一源路径触发第一次重命名（如 watcher 的重复事件）
        InvokeOnRenamed(_watcher, _syncRoot, "dup-src.txt", "dup-dst1.txt");

        // 等待第一次入队完成
        var first = await WaitUntilAsync(async () =>
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            return await db.SyncQueue.FirstOrDefaultAsync(q => q.FilePath == "/dup-src.txt");
        });
        Assert.NotNull(first);

        // Act：同一源路径再次重命名
        InvokeOnRenamed(_watcher, _syncRoot, "dup-src.txt", "dup-dst2.txt");

        // Assert：仍是同一条记录，TargetPath 更新为最新目标
        var items = await WaitUntilAsync(async () =>
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var list = await db.SyncQueue.Where(q => q.FilePath == "/dup-src.txt").ToListAsync();
            return list.Count == 1 && list[0].TargetPath == "/dup-dst2.txt" ? list : null;
        });
        Assert.NotNull(items);
        Assert.Equal("/dup-dst2.txt", items![0].TargetPath);
    }

    [Fact]
    public async Task 忽略文件_不触发入队()
    {
        // Arrange
        string hiddenDir = Path.Combine(_syncRoot, ".cloudpan");
        Directory.CreateDirectory(hiddenDir);

        // Act：模拟 .tmp、~*、.cloudpan 下文件的事件
        InvokeOnChanged(_watcher, _syncRoot, "temp.tmp");
        InvokeOnChanged(_watcher, _syncRoot, "~$office.docx");
        InvokeOnChanged(_watcher, hiddenDir, "db.sqlite");
        await Task.Delay(500); // 等待 async void 处理完成

        // Assert：不应有任何入队记录
        await using var db = await _dbFactory.CreateDbContextAsync();
        int count = await db.SyncQueue.CountAsync(q =>
            q.FilePath == "/temp.tmp" || q.FilePath == "/~$office.docx" || q.FilePath == "/.cloudpan/db.sqlite");
        Assert.Equal(0, count);
    }

    // ============================================================
    // 辅助方法
    // ============================================================

    /// <summary>反射调用私有 ShouldIgnore 方法。</summary>
    private static bool InvokeShouldIgnore(FileWatcherService service, string fullPath)
    {
        var method = typeof(FileWatcherService).GetMethod("ShouldIgnore", NonPublicInstance)
            ?? throw new InvalidOperationException("未找到 FileWatcherService.ShouldIgnore");
        return (bool)method.Invoke(service, [fullPath])!;
    }

    /// <summary>反射调用私有 OnRenamed 处理程序（模拟 watcher 重命名事件）。</summary>
    private static void InvokeOnRenamed(FileWatcherService service, string directory, string oldName, string newName)
    {
        var method = typeof(FileWatcherService).GetMethod("OnRenamed", NonPublicInstance)
            ?? throw new InvalidOperationException("未找到 FileWatcherService.OnRenamed");
        method.Invoke(service, [null!, new RenamedEventArgs(WatcherChangeTypes.Renamed, directory, newName, oldName)]);
    }

    /// <summary>反射调用私有 OnChanged 处理程序（模拟 watcher 创建事件）。</summary>
    private static void InvokeOnChanged(FileWatcherService service, string directory, string fileName)
    {
        var method = typeof(FileWatcherService).GetMethod("OnChanged", NonPublicInstance)
            ?? throw new InvalidOperationException("未找到 FileWatcherService.OnChanged");
        method.Invoke(service, [null!, new FileSystemEventArgs(WatcherChangeTypes.Created, directory, fileName)]);
    }

    /// <summary>轮询等待异步条件成立（处理 async void 事件与真实 watcher 事件延迟）。</summary>
    private static async Task<T?> WaitUntilAsync<T>(Func<Task<T?>> check, int timeoutMs = 5000) where T : class
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            var result = await check();
            if (result != null)
            {
                return result;
            }

            await Task.Delay(100);
        }
        return null;
    }
}
