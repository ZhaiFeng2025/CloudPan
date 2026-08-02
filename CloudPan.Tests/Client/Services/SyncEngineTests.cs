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
        string dbPath = Path.Combine(_tempDir, "client-test.db");
        _dbFactory = new TestClientDbFactory(dbPath);
        using (var db = _dbFactory.CreateDbContext())
        {
            db.Database.EnsureCreated();
            db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
        }

        _api = new MockApiClient();
        _logger = NullLoggerFactory.Instance.CreateLogger<SyncEngine>();
        SyncConfig config = new SyncConfig { SyncRoot = _syncRoot, ServerUrl = "http://localhost:8443" };

        _engine = new SyncEngine(_api, config, _dbFactory, _logger);
    }

    /// <summary>冲突测试事件处理器（Dispose 中退订，满足 CP300 事件订阅可退订规则）。</summary>
    private Action<ConflictInfo>? _conflictHandler;

    public void Dispose()
    {
        if (_conflictHandler != null)
        {
            _engine.ConflictDetected -= _conflictHandler;
        }
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    // ============================================================
    // EnqueueLocalChangeAsync 测试
    // ============================================================

    [Fact]
    public async Task EnqueueLocalChange_新文件_入队上传()
    {
        string filePath = Path.Combine(_syncRoot, "upload-me.txt");
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
        string filePath = Path.Combine(_syncRoot, "dup.txt");
        await File.WriteAllTextAsync(filePath, "test");

        // 第一次入队
        await _engine.EnqueueLocalChangeAsync("/dup.txt", SyncOperation.Upload);

        // 修改文件（改变大小以绕过大小去重）
        await File.WriteAllTextAsync(filePath, "test-modified-larger");

        // 第二次入队——应该被去重（同一操作已在队列中）
        await _engine.EnqueueLocalChangeAsync("/dup.txt", SyncOperation.Upload);

        await using var dbCheck = await _dbFactory.CreateDbContextAsync();
        int count = await dbCheck.SyncQueue.CountAsync(q => q.FilePath == "/dup.txt");
        Assert.Equal(1, count); // 只有一条
    }

    [Fact]
    public async Task EnqueueLocalChange_删除冲销待上传项()
    {
        string filePath = Path.Combine(_syncRoot, "cancel-me.txt");
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
        string filePath = Path.Combine(_syncRoot, "skip-me.txt");
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

        // 触发上传——文件大小未变（4 bytes），但快照无哈希记录，应上传以确保内容一致
        await _engine.EnqueueLocalChangeAsync("/skip-me.txt", SyncOperation.Upload);

        await using var dbCheck = await _dbFactory.CreateDbContextAsync();
        int count = await dbCheck.SyncQueue.CountAsync(q => q.FilePath == "/skip-me.txt");
        Assert.Equal(1, count); // 无哈希时上传以确保内容一致
    }

    [Fact]
    public async Task EnqueueLocalChange_文件不存在_跳过上传()
    {
        // 不存在的文件——不应入队
        await _engine.EnqueueLocalChangeAsync("/ghost.txt", SyncOperation.Upload);

        await using var dbCheck = await _dbFactory.CreateDbContextAsync();
        int count = await dbCheck.SyncQueue.CountAsync();
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
        string filePath = Path.Combine(_syncRoot, "changed.txt");
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
        string hiddenDir = Path.Combine(_syncRoot, ".cloudpan");
        Directory.CreateDirectory(hiddenDir);
        await File.WriteAllTextAsync(Path.Combine(hiddenDir, "internal.txt"), "hidden");

        // 创建 .tmp 文件——应被忽略
        await File.WriteAllTextAsync(Path.Combine(_syncRoot, "temp.tmp"), "tmp");

        await _engine.FullScanAsync();

        await using var dbCheck = await _dbFactory.CreateDbContextAsync();
        int count = await dbCheck.SyncQueue.CountAsync();
        Assert.Equal(0, count); // 全部被忽略
    }

    // ============================================================
    // 小文件优先排序测试
    // ============================================================

    [Fact]
    public async Task EnqueueLocalChange_小文件_高优先级()
    {
        string filePath = Path.Combine(_syncRoot, "small.bin");
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
        string filePath = Path.Combine(_syncRoot, "big.bin");
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

    // ============================================================
    // ProcessQueueAsync 测试（通过 StartAsync 间接测试）
    // ============================================================

    [Fact]
    public async Task WsFileDeleted_删除本地副本并清理快照()
    {
        // 预置本地文件 + 远端快照 + 待处理上传
        string filePath = Path.Combine(_syncRoot, "ws-del.txt");
        await File.WriteAllTextAsync(filePath, "will be deleted by ws");
        await using (var setupDb = await _dbFactory.CreateDbContextAsync())
        {
            setupDb.RemoteSnapshots.Add(new RemoteSnapshot
            {
                Path = "/ws-del.txt", Type = 0, Size = 27,
                Version = 2, State = 0, Hash = "hash"
            });
            setupDb.SyncQueue.Add(new SyncQueueItem
            {
                FilePath = "/ws-del.txt", Operation = (int)SyncOperation.Upload,
                Priority = (int)QueuePriority.High
            });
            await setupDb.SaveChangesAsync();
        }

        // 反射调用 DeleteLocalCopyAsync（WS file_deleted 精确处理路径）
        var method = typeof(SyncEngine).GetMethod("DeleteLocalCopyAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(method);
        Task? task = (Task?)method!.Invoke(_engine, ["/ws-del.txt"]);
        Assert.NotNull(task);
        await task;

        // 本地文件已删、快照已清、待处理队列已取消
        Assert.False(File.Exists(filePath));
        await using var dbCheck = await _dbFactory.CreateDbContextAsync();
        Assert.Null(await dbCheck.RemoteSnapshots.FindAsync("/ws-del.txt"));
        int pending = await dbCheck.SyncQueue.CountAsync(q => q.FilePath == "/ws-del.txt");
        Assert.Equal(0, pending);
    }

    [Fact]
    public async Task ApplyRemoteChanges_Deleting墓碑_删除本地文件()
    {
        // 预置本地文件（服务端已删除，树返回 Deleting 墓碑）
        string filePath = Path.Combine(_syncRoot, "tomb-del.txt");
        await File.WriteAllTextAsync(filePath, "to be deleted by tombstone");

        var response = new FileTreeResponse(
            new[]
            {
                new FileEntryDto("/tomb-del.txt", (int)CloudPan.Shared.FileType.File,
                    "hash", 30, 5, DateTime.UtcNow.ToString("O"), (int)CloudPan.Shared.FileState.Deleting)
            },
            null, false, 5);

        var method = typeof(SyncEngine).GetMethod("ApplyRemoteChangesAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(method);

        await using var db = await _dbFactory.CreateDbContextAsync();
        Task? task = (Task?)method!.Invoke(_engine, [db, response, CancellationToken.None]);
        Assert.NotNull(task);
        await task;

        Assert.False(File.Exists(filePath)); // 本地副本已删除
    }

    [Fact]
    public async Task ProcessQueue_上传成功_从队列移除()
    {
        string filePath = Path.Combine(_syncRoot, "process-upload.txt");
        await File.WriteAllTextAsync(filePath, "test content for upload");

        // 入队上传后直接调用 ProcessQueueAsync（不启动完整引擎）
        await _engine.EnqueueLocalChangeAsync("/process-upload.txt", SyncOperation.Upload);

        // 调用反射执行私有 ProcessQueueAsync 方法
        var method = typeof(SyncEngine).GetMethod("ProcessQueueAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        if (method != null)
        {
            Task? task = (Task?)method.Invoke(_engine, [CancellationToken.None]);
            if (task != null)
            {
                await task;
            }
        }

        // 验证 MockApiClient 收到上传调用
        Assert.True(_api.UploadCalls.ContainsKey("/process-upload.txt"));

        // 验证队列项已移除
        await using var dbCheck = await _dbFactory.CreateDbContextAsync();
        int remaining = await dbCheck.SyncQueue.CountAsync(q => q.FilePath == "/process-upload.txt");
        Assert.Equal(0, remaining);
    }

    // ============================================================
    // 上传冲突检测 BaseVersion 接线测试（F-06）
    // ============================================================

    [Fact]
    public async Task EnqueueLocalChange_上传_记录BaseVersion为快照版本()
    {
        // 预置已同步快照（本地上一次已同步版本 = 3）
        string filePath = Path.Combine(_syncRoot, "base-ver.txt");
        await File.WriteAllTextAsync(filePath, "content-v2");

        await using (var setupDb = await _dbFactory.CreateDbContextAsync())
        {
            setupDb.RemoteSnapshots.Add(new RemoteSnapshot
            {
                Path = "/base-ver.txt", Type = 0, Size = 999, // 大小与本地不同，绕过去重
                Version = 3, State = 0, Hash = "old-hash"
            });
            await setupDb.SaveChangesAsync();
        }

        await _engine.EnqueueLocalChangeAsync("/base-ver.txt", SyncOperation.Upload);

        await using var dbCheck = await _dbFactory.CreateDbContextAsync();
        var item = await dbCheck.SyncQueue.FirstOrDefaultAsync(q => q.FilePath == "/base-ver.txt");
        Assert.NotNull(item);
        Assert.Equal(3, item!.BaseVersion); // BaseVersion = snapshot.Version
    }

    [Fact]
    public async Task EnqueueLocalChange_新文件上传_BaseVersion为空()
    {
        string filePath = Path.Combine(_syncRoot, "brand-new.txt");
        await File.WriteAllTextAsync(filePath, "new file");

        await _engine.EnqueueLocalChangeAsync("/brand-new.txt", SyncOperation.Upload);

        await using var dbCheck = await _dbFactory.CreateDbContextAsync();
        var item = await dbCheck.SyncQueue.FirstOrDefaultAsync(q => q.FilePath == "/brand-new.txt");
        Assert.NotNull(item);
        Assert.Null(item!.BaseVersion); // 无快照（新文件），BaseVersion 为空 → 服务端按新文件处理
    }

    [Fact]
    public async Task ProcessUpload_双设备并发编辑_第二次上传409_Conflict冲突提示()
    {
        string filePath = Path.Combine(_syncRoot, "concurrent-edit.txt");
        await File.WriteAllTextAsync(filePath, "设备B的编辑");

        // 设备 A 首次上传（服务端 v1）——设备 B 同步后本地快照为 v1
        await _api.UploadAsync(filePath, "/concurrent-edit.txt", baseVersion: 0, lastModified: DateTime.UtcNow.ToString("O"));

        await using (var setupDb = await _dbFactory.CreateDbContextAsync())
        {
            setupDb.RemoteSnapshots.Add(new RemoteSnapshot
            {
                Path = "/concurrent-edit.txt", Type = 0, Size = new FileInfo(filePath).Length,
                Version = 1, State = 0, Hash = "hash-v1"
            });
            await setupDb.SaveChangesAsync();
        }

        // 设备 A 再次编辑上传（服务端 v2，设备 B 不知情）
        await _api.UploadAsync(filePath, "/concurrent-edit.txt", baseVersion: 1, lastModified: DateTime.UtcNow.ToString("O"));

        // 设备 B 本地编辑 → 入队上传（BaseVersion 应为快照版本 1）
        await _engine.EnqueueLocalChangeAsync("/concurrent-edit.txt", SyncOperation.Upload);

        await using (var enqueueDb = await _dbFactory.CreateDbContextAsync())
        {
            var item = await enqueueDb.SyncQueue.FirstOrDefaultAsync(q => q.FilePath == "/concurrent-edit.txt");
            Assert.NotNull(item);
            Assert.Equal(1, item!.BaseVersion);
        }

        // 订阅冲突事件（具名字段处理器，Dispose 中退订满足 CP300）
        int conflictCount = 0;
        ConflictInfo? conflictInfo = null;
        _conflictHandler = ci => { conflictCount++; conflictInfo = ci; };
        _engine.ConflictDetected += _conflictHandler;

        // 处理队列 → 服务端 409 → 触发客户端冲突提示
        var method = typeof(SyncEngine).GetMethod("ProcessQueueAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(method);
        Task? task = (Task?)method!.Invoke(_engine, [CancellationToken.None]);
        Assert.NotNull(task);
        await task;

        Assert.Equal(1, conflictCount); // 冲突提示已触发
        Assert.NotNull(conflictInfo);
        Assert.Equal("/concurrent-edit.txt", conflictInfo!.RelativePath);

        // 队列项保留（等待用户决策，而非被删除或覆盖）
        await using var dbFinal = await _dbFactory.CreateDbContextAsync();
        int remaining = await dbFinal.SyncQueue.CountAsync(q => q.FilePath == "/concurrent-edit.txt");
        Assert.Equal(1, remaining);
    }

    // ============================================================
    // 每文件同步状态查询测试（T-009）
    // ============================================================

    [Fact]
    public async Task GetFileSyncStatuses_已同步文件_状态为Synced且本地存在()
    {
        string filePath = Path.Combine(_syncRoot, "synced.txt");
        await File.WriteAllTextAsync(filePath, "content");
        await using (var setupDb = await _dbFactory.CreateDbContextAsync())
        {
            setupDb.RemoteSnapshots.Add(new RemoteSnapshot
            {
                Path = "/synced.txt", Type = (int)FileType.File,
                Size = 7, Version = 1, State = (int)FileState.Synced, Hash = "h"
            });
            await setupDb.SaveChangesAsync();
        }

        var statuses = await _engine.GetFileSyncStatusesAsync();
        var item = statuses.Single(s => s.RelativePath == "/synced.txt");
        Assert.Equal((int)FileState.Synced, item.State);
        Assert.True(item.LocalExists);
    }

    [Fact]
    public async Task GetFileSyncStatuses_CloudOnly文件_本地无副本()
    {
        await using (var setupDb = await _dbFactory.CreateDbContextAsync())
        {
            setupDb.RemoteSnapshots.Add(new RemoteSnapshot
            {
                Path = "/cloud-only.txt", Type = (int)FileType.File,
                Size = 5, Version = 2, State = (int)FileState.CloudOnly, Hash = "h"
            });
            await setupDb.SaveChangesAsync();
        }

        var statuses = await _engine.GetFileSyncStatusesAsync();
        var item = statuses.Single(s => s.RelativePath == "/cloud-only.txt");
        Assert.Equal((int)FileState.CloudOnly, item.State);
        Assert.False(item.LocalExists);
    }

    [Fact]
    public async Task GetFileSyncStatuses_待上传队列项_状态为Uploading()
    {
        string filePath = Path.Combine(_syncRoot, "pending-upload.txt");
        await File.WriteAllTextAsync(filePath, "pending");
        await using (var setupDb = await _dbFactory.CreateDbContextAsync())
        {
            setupDb.SyncQueue.Add(new SyncQueueItem
            {
                FilePath = "/pending-upload.txt",
                Operation = (int)SyncOperation.Upload,
                Priority = (int)QueuePriority.High
            });
            await setupDb.SaveChangesAsync();
        }

        var statuses = await _engine.GetFileSyncStatusesAsync();
        var item = statuses.Single(s => s.RelativePath == "/pending-upload.txt");
        Assert.Equal((int)FileState.Uploading, item.State);
    }

    [Fact]
    public async Task GetFileSyncStatuses_本地新文件_状态为Modified待上传()
    {
        string filePath = Path.Combine(_syncRoot, "local-only.txt");
        await File.WriteAllTextAsync(filePath, "new local");

        var statuses = await _engine.GetFileSyncStatusesAsync();
        var item = statuses.Single(s => s.RelativePath == "/local-only.txt");
        Assert.Equal((int)FileState.Modified, item.State);
        Assert.True(item.LocalExists);
    }
}

/// <summary>测试用 ClientDbContext 工厂。</summary>
internal class TestClientDbFactory : IDbContextFactory<ClientDbContext>
{
    private readonly string _dbPath;
    public TestClientDbFactory(string dbPath) => _dbPath = dbPath;
    public ClientDbContext CreateDbContext() => new(_dbPath);
}
