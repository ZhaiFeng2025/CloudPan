using CloudPan.Client.Models;
using CloudPan.Client.Services;
using CloudPan.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CloudPan.Tests.Client.Services;

/// <summary>
/// SyncEngine 单元测试——利用 MockApiClient 验证同步引擎核心逻辑。
/// </summary>
public class SyncEngineTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _syncRoot;
    private readonly MockApiClient _api;
    private readonly IDbContextFactory<ClientDbContext> _dbFactory;
    private readonly SyncEngine _engine;
    private readonly ILogger<SyncEngine> _logger;

    public SyncEngineTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"CloudPanSyncEngine_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        // 同步根使用子目录，避免测试数据库文件被 FullScan 扫描到
        _syncRoot = Path.Combine(_tempDir, "sync");
        Directory.CreateDirectory(_syncRoot);

        // 测试数据库放在同步根之外
        var dbPath = Path.Combine(_tempDir, "client-test.db");
        _dbFactory = new TestClientDbFactory(dbPath);
        using (var db = _dbFactory.CreateDbContext())
        {
            db.Database.EnsureCreated();
            db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
        }

        _api = new MockApiClient();
        _logger = NullLoggerFactory.Instance.CreateLogger<SyncEngine>();
        var config = new SyncConfig { SyncRoot = _syncRoot, ServerUrl = "http://localhost:8443" };

        _engine = new SyncEngine(_api, config, _dbFactory, _logger);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    // ============================================================
    // EnqueueLocalChangeAsync 测试
    // ============================================================

    [Fact]
    public async Task EnqueueLocalChange_新文件_入队上传()
    {
        var filePath = Path.Combine(_syncRoot, "upload-me.txt");
        await File.WriteAllTextAsync(filePath, "hello sync");

        await _engine.EnqueueLocalChangeAsync("/upload-me.txt", SyncOperation.Upload);

        await using var dbCheck = await _dbFactory.CreateDbContextAsync();
        var queue = await dbCheck.SyncQueue.FirstOrDefaultAsync(q => q.FilePath == "/upload-me.txt");
        Assert.NotNull(queue);
        Assert.Equal((int)SyncOperation.Upload, queue.Operation);
    }

    [Fact]
    public async Task EnqueueLocalChange_重复操作_去重()
    {
        var filePath = Path.Combine(_syncRoot, "dup.txt");
        await File.WriteAllTextAsync(filePath, "test");

        // 第一次入队
        await _engine.EnqueueLocalChangeAsync("/dup.txt", SyncOperation.Upload);

        // 修改文件（改变大小以绕过大小去重）
        await File.WriteAllTextAsync(filePath, "test-modified-larger");

        // 第二次入队——应该被去重（同一操作已在队列中）
        await _engine.EnqueueLocalChangeAsync("/dup.txt", SyncOperation.Upload);

        await using var dbCheck = await _dbFactory.CreateDbContextAsync();
        var count = await dbCheck.SyncQueue.CountAsync(q => q.FilePath == "/dup.txt");
        Assert.Equal(1, count); // 只有一条
    }

    [Fact]
    public async Task EnqueueLocalChange_删除冲销待上传项()
    {
        var filePath = Path.Combine(_syncRoot, "cancel-me.txt");
        await File.WriteAllTextAsync(filePath, "will be deleted");

        // 先入队上传
        await _engine.EnqueueLocalChangeAsync("/cancel-me.txt", SyncOperation.Upload);

        // 再入队删除——应取消上传并替换为删除
        await _engine.EnqueueLocalChangeAsync("/cancel-me.txt", SyncOperation.Delete);

        await using var dbCheck = await _dbFactory.CreateDbContextAsync();
        var items = await dbCheck.SyncQueue.Where(q => q.FilePath == "/cancel-me.txt").ToListAsync();
        Assert.Single(items);
        Assert.Equal((int)SyncOperation.Delete, items[0].Operation);
    }

    [Fact]
    public async Task EnqueueLocalChange_大小未变_跳过上传()
    {
        var filePath = Path.Combine(_syncRoot, "skip-me.txt");
        await File.WriteAllTextAsync(filePath, "AAAA"); // 4 bytes

        // 先创建快照
        await using (var setupDb = await _dbFactory.CreateDbContextAsync())
        {
            setupDb.RemoteSnapshots.Add(new RemoteSnapshot
            {
                Path = "/skip-me.txt",
                Type = 0,
                Size = 4,
                Version = 1,
                State = 0
            });
            await setupDb.SaveChangesAsync();
        }

        // 触发上传——文件大小未变（4 bytes），应跳过
        await _engine.EnqueueLocalChangeAsync("/skip-me.txt", SyncOperation.Upload);

        await using var dbCheck = await _dbFactory.CreateDbContextAsync();
        var count = await dbCheck.SyncQueue.CountAsync(q => q.FilePath == "/skip-me.txt");
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task EnqueueLocalChange_文件不存在_跳过上传()
    {
        // 不存在的文件——不应入队
        await _engine.EnqueueLocalChangeAsync("/ghost.txt", SyncOperation.Upload);

        await using var dbCheck = await _dbFactory.CreateDbContextAsync();
        var count = await dbCheck.SyncQueue.CountAsync();
        Assert.Equal(0, count);
    }

    // ============================================================
    // FullScanAsync 测试
    // ============================================================

    [Fact]
    public async Task FullScan_新文件_入队上传()
    {
        await File.WriteAllTextAsync(Path.Combine(_syncRoot, "new-file.txt"), "fresh");

        await _engine.FullScanAsync();

        await using var dbCheck = await _dbFactory.CreateDbContextAsync();
        var item = await dbCheck.SyncQueue.FirstOrDefaultAsync(q => q.FilePath == "/new-file.txt");
        Assert.NotNull(item);
        Assert.Equal((int)SyncOperation.Upload, item.Operation);
    }

    [Fact]
    public async Task FullScan_文件变更_入队上传()
    {
        var filePath = Path.Combine(_syncRoot, "changed.txt");
        await File.WriteAllTextAsync(filePath, "AAA"); // 3 bytes

        // 快照记录为 10 bytes
        await using (var setupDb = await _dbFactory.CreateDbContextAsync())
        {
            setupDb.RemoteSnapshots.Add(new RemoteSnapshot
            {
                Path = "/changed.txt", Type = 0, Size = 10,
                Version = 1, State = 0, Hash = "old-hash"
            });
            await setupDb.SaveChangesAsync();
        }

        await _engine.FullScanAsync();

        await using var dbCheck = await _dbFactory.CreateDbContextAsync();
        var item = await dbCheck.SyncQueue.FirstOrDefaultAsync(q => q.FilePath == "/changed.txt");
        Assert.NotNull(item); // 大小不一致，应入队
    }

    [Fact]
    public async Task FullScan_本地删除_入队删除()
    {
        // 快照中有文件，但本地没有
        await using (var setupDb = await _dbFactory.CreateDbContextAsync())
        {
            setupDb.RemoteSnapshots.Add(new RemoteSnapshot
            {
                Path = "/deleted-locally.txt", Type = 0, Size = 5,
                Version = 2, State = 0, Hash = "hash"
            });
            await setupDb.SaveChangesAsync();
        }

        await _engine.FullScanAsync();

        await using var dbCheck = await _dbFactory.CreateDbContextAsync();
        var item = await dbCheck.SyncQueue.FirstOrDefaultAsync(q => q.FilePath == "/deleted-locally.txt");
        Assert.NotNull(item);
        Assert.Equal((int)SyncOperation.Delete, item.Operation);
    }

    [Fact]
    public async Task FullScan_忽略隐藏文件和临时文件()
    {
        // 创建 .cloudpan 下的文件——应被忽略
        var hiddenDir = Path.Combine(_syncRoot, ".cloudpan");
        Directory.CreateDirectory(hiddenDir);
        await File.WriteAllTextAsync(Path.Combine(hiddenDir, "internal.txt"), "hidden");

        // 创建 .tmp 文件——应被忽略
        await File.WriteAllTextAsync(Path.Combine(_syncRoot, "temp.tmp"), "tmp");

        await _engine.FullScanAsync();

        await using var dbCheck = await _dbFactory.CreateDbContextAsync();
        var count = await dbCheck.SyncQueue.CountAsync();
        Assert.Equal(0, count); // 全部被忽略
    }

    // ============================================================
    // 小文件优先排序测试
    // ============================================================

    [Fact]
    public async Task EnqueueLocalChange_小文件_高优先级()
    {
        var filePath = Path.Combine(_syncRoot, "small.bin");
        await File.WriteAllBytesAsync(filePath, new byte[500_000]); // 500KB < 1MB threshold

        await _engine.EnqueueLocalChangeAsync("/small.bin", SyncOperation.Upload);

        await using var dbCheck = await _dbFactory.CreateDbContextAsync();
        var item = await dbCheck.SyncQueue.FirstOrDefaultAsync(q => q.FilePath == "/small.bin");
        Assert.NotNull(item);
        Assert.Equal((int)QueuePriority.High, item.Priority);
    }

    [Fact]
    public async Task EnqueueLocalChange_大文件_普通优先级()
    {
        // 创建 2MB 文件
        var filePath = Path.Combine(_syncRoot, "big.bin");
        await using (var fs = File.Create(filePath))
        {
            fs.SetLength(2_097_152); // 2MB > 1MB threshold
        }

        await _engine.EnqueueLocalChangeAsync("/big.bin", SyncOperation.Upload);

        await using var dbCheck = await _dbFactory.CreateDbContextAsync();
        var item = await dbCheck.SyncQueue.FirstOrDefaultAsync(q => q.FilePath == "/big.bin");
        Assert.NotNull(item);
        Assert.Equal((int)QueuePriority.Normal, item.Priority);
    }
}

/// <summary>测试用 ClientDbContext 工厂。</summary>
internal class TestClientDbFactory : IDbContextFactory<ClientDbContext>
{
    private readonly string _dbPath;
    public TestClientDbFactory(string dbPath) => _dbPath = dbPath;
    public ClientDbContext CreateDbContext() => new(_dbPath);
}
