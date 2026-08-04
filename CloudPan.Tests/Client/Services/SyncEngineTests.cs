using CloudPan.Client.Core.Models;
using CloudPan.Client.Core.Services;
using CloudPan.Contract;
using CloudPan.Infrastructure.Models;
using CloudPan.Infrastructure.Persistence.Client;
using CloudPan.Infrastructure.Storage;
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
    private readonly IClientStoreFactory _storeFactory;
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
        // T-093：领域层经 IClientStore 抽象访问持久化（测试复用 Infrastructure 的 EF 实现工厂）
        _storeFactory = new ClientStoreFactory(_dbFactory);

        _api = new MockApiClient();
        _logger = NullLoggerFactory.Instance.CreateLogger<SyncEngine>();
        SyncConfig config = new SyncConfig { SyncRoot = _syncRoot, ServerUrl = "http://localhost:8443" };

        _engine = new SyncEngine(_api, config, _storeFactory, _logger);
    }

    /// <summary>冲突测试事件处理器（Dispose 中退订，满足 CP300 事件订阅可退订规则）。</summary>
    private Action<ConflictInfo>? _conflictHandler;

    /// <summary>重配引导测试事件处理器（Dispose 中退订，满足 CP300 事件订阅可退订规则）。</summary>
    private Action? _reconfigHandler;

    /// <summary>同步错误测试事件处理器（T-098：删除冲突本地文件缺失容错断言白话提示）。</summary>
    private Action<string, ErrorAttribution, SyncOperation>? _errorHandler;

    public void Dispose()
    {
        if (_conflictHandler != null)
        {
            _engine.ConflictDetected -= _conflictHandler;
        }
        if (_reconfigHandler != null)
        {
            _engine.ReconfigurationRequired -= _reconfigHandler;
        }
        if (_errorHandler != null)
        {
            _engine.ErrorOccurred -= _errorHandler;
        }
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    /// <summary>反射调用内部 ProcessQueueAsync（现有测试通用模式）。</summary>
    private async Task InvokeProcessQueueAsync()
    {
        var method = typeof(SyncEngine).GetMethod("ProcessQueueAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(method);
        Task? task = (Task?)method!.Invoke(_engine, [CancellationToken.None]);
        Assert.NotNull(task);
        await task;
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
        // 快照中有文件（曾落盘 IsDownloaded=true），但本地没有 → 正常传播删除
        await using (var setupDb = await _dbFactory.CreateDbContextAsync())
        {
            setupDb.RemoteSnapshots.Add(new RemoteSnapshot
            {
                Path = "/deleted-locally.txt", Type = (int)FileType.File, Size = 5,
                Version = 2, State = (int)FileState.Synced, Hash = "hash", IsDownloaded = true
            });
            await setupDb.SaveChangesAsync();
        }

        await _engine.FullScanAsync();

        await using var dbCheck = await _dbFactory.CreateDbContextAsync();
        var item = await dbCheck.SyncQueue.FirstOrDefaultAsync(q => q.FilePath == "/deleted-locally.txt");
        Assert.NotNull(item);
        Assert.Equal((int)SyncOperation.Delete, item.Operation);
    }

    // T-037：下载窗口保护——远端新文件首次下载未完成时不误删
    [Fact]
    public async Task FullScan_下载未完成快照_本地缺失_不误删()
    {
        // 场景（F-37）：另一设备上传的新文件落在『快照已建但下载未完成』窗口内——本地无文件，
        // 快照 IsDownloaded=false（下载完成前不得视为已落盘）
        await using (var setupDb = await _dbFactory.CreateDbContextAsync())
        {
            setupDb.RemoteSnapshots.Add(new RemoteSnapshot
            {
                Path = "/pending-download.txt", Type = (int)FileType.File, Size = 100,
                Version = 1, State = (int)FileState.Synced, Hash = "hash", IsDownloaded = false
            });
            await setupDb.SaveChangesAsync();
        }

        // 5 分钟兜底全量扫描
        await _engine.FullScanAsync();

        await using var dbCheck = await _dbFactory.CreateDbContextAsync();
        // 不误删：既不入队 Delete，也不取消未决下载
        int deleteCount = await dbCheck.SyncQueue.CountAsync(q =>
            q.FilePath == "/pending-download.txt" && q.Operation == (int)SyncOperation.Delete);
        Assert.Equal(0, deleteCount);
    }

    // T-037 第 3 点：扫描入队 Delete 前检查 SyncQueue 未决下载项
    [Fact]
    public async Task FullScan_存在未决下载项_已落盘快照本地缺失_不误删()
    {
        // 场景：快照曾落盘（IsDownloaded=true）但本地当前缺失，SyncQueue 存在该路径未决下载项
        //（如远端更新后的重下载窗口）→ 跳过删除判定，待下载完成后再判定
        await using (var setupDb = await _dbFactory.CreateDbContextAsync())
        {
            setupDb.RemoteSnapshots.Add(new RemoteSnapshot
            {
                Path = "/redownload.txt", Type = (int)FileType.File, Size = 100,
                Version = 2, State = (int)FileState.Synced, Hash = "hash", IsDownloaded = true
            });
            setupDb.SyncQueue.Add(new SyncQueue
            {
                FilePath = "/redownload.txt", Operation = (int)SyncOperation.Download,
                Priority = (int)QueuePriority.Normal, BaseVersion = 2
            });
            await setupDb.SaveChangesAsync();
        }

        await _engine.FullScanAsync();

        await using var dbCheck = await _dbFactory.CreateDbContextAsync();
        // 不判定删除
        int deleteCount = await dbCheck.SyncQueue.CountAsync(q =>
            q.FilePath == "/redownload.txt" && q.Operation == (int)SyncOperation.Delete);
        Assert.Equal(0, deleteCount);
        // 未决下载项保留（未被 EnqueueLocalChangeAsync(Delete) 取消）
        Assert.Equal(1, await dbCheck.SyncQueue.CountAsync(q =>
            q.FilePath == "/redownload.txt" && q.Operation == (int)SyncOperation.Download));
    }

    // T-049：FullScan 目录删除兜底——本地目录缺失且快照为目录（曾在本机物化 IsDownloaded=true）→ 入队 Delete
    [Fact]
    public async Task FullScan_本地目录缺失_快照为目录_入队Delete()
    {
        // 快照中目录曾在本机物化（本机创建并同步），但本地目录已被删除 → 5 分钟兜底扫描应传播删除
        await using (var setupDb = await _dbFactory.CreateDbContextAsync())
        {
            setupDb.RemoteSnapshots.Add(new RemoteSnapshot
            {
                Path = "/photos", Type = (int)FileType.Directory,
                Version = 3, State = (int)FileState.Synced, IsDownloaded = true
            });
            await setupDb.SaveChangesAsync();
        }

        await _engine.FullScanAsync();

        await using var dbCheck = await _dbFactory.CreateDbContextAsync();
        var item = await dbCheck.SyncQueue.FirstOrDefaultAsync(q => q.FilePath == "/photos");
        Assert.NotNull(item);
        Assert.Equal((int)SyncOperation.Delete, item.Operation);
    }

    // T-049：远端未物化目录快照（IsDownloaded=false）本地缺失 → 不误判删除（防删除-重建振荡）
    [Fact]
    public async Task FullScan_远端未物化目录快照_本地缺失_不误删()
    {
        // 远端空目录快照（ApplyRemoteChanges 创建，IsDownloaded=false，未在本机物化）
        await using (var setupDb = await _dbFactory.CreateDbContextAsync())
        {
            setupDb.RemoteSnapshots.Add(new RemoteSnapshot
            {
                Path = "/photos", Type = (int)FileType.Directory,
                Version = 2, State = (int)FileState.Synced, IsDownloaded = false
            });
            await setupDb.SaveChangesAsync();
        }

        await _engine.FullScanAsync();

        await using var dbCheck = await _dbFactory.CreateDbContextAsync();
        // 不误删：远端空目录不应被本机 FullScan 判定为本地删除（否则删除-重建振荡）
        int deleteCount = await dbCheck.SyncQueue.CountAsync(q =>
            q.FilePath == "/photos" && q.Operation == (int)SyncOperation.Delete);
        Assert.Equal(0, deleteCount);
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

    [Fact]
    public async Task FullScan_取消勾选CloudOnly本地残留副本_不重传不振荡()
    {
        // 场景（F-23）：/photos 目录已取消勾选（排除集含 /photos/），快照 State==CloudOnly，
        // 本地仍残留此前下载的副本
        SetSelectedPaths(_engine, new List<string> { "/photos/" });
        string filePath = Path.Combine(_syncRoot, "photos", "summer.jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        await File.WriteAllTextAsync(filePath, "jpeg-data");

        await using (var setupDb = await _dbFactory.CreateDbContextAsync())
        {
            setupDb.RemoteSnapshots.Add(new RemoteSnapshot
            {
                Path = "/photos/summer.jpg", Type = (int)FileType.File,
                Size = 9, Version = 3, State = (int)FileState.CloudOnly, Hash = "cloud-hash"
            });
            await setupDb.SaveChangesAsync();
        }

        // 第一次全量扫描：CloudOnly 本地残留副本不应作为新文件入队上传
        await _engine.FullScanAsync();
        await using (var dbCheck = await _dbFactory.CreateDbContextAsync())
        {
            int uploadCount = await dbCheck.SyncQueue.CountAsync(q =>
                q.FilePath == "/photos/summer.jpg" && q.Operation == (int)SyncOperation.Upload);
            Assert.Equal(0, uploadCount); // 不入队上传

            var snapshot = await dbCheck.RemoteSnapshots.FindAsync("/photos/summer.jpg");
            Assert.NotNull(snapshot);
            Assert.Equal((int)FileState.CloudOnly, snapshot!.State); // 快照保持 CloudOnly（未被上传置回 Synced）
        }

        // 第二次全量扫描：仍不入队上传 → 不振荡
        await _engine.FullScanAsync();
        await using (var dbCheck2 = await _dbFactory.CreateDbContextAsync())
        {
            int uploadCount = await dbCheck2.SyncQueue.CountAsync(q =>
                q.FilePath == "/photos/summer.jpg" && q.Operation == (int)SyncOperation.Upload);
            Assert.Equal(0, uploadCount);
        }
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
            setupDb.SyncQueue.Add(new SyncQueue
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
                new FileEntryDto("/tomb-del.txt", (int)CloudPan.Contract.FileType.File,
                    "hash", 30, 5, DateTime.UtcNow.ToString("O"), (int)CloudPan.Contract.FileState.Deleting)
            },
            null, false, 5);

        await using var store = await _storeFactory.CreateStoreAsync();
        await CallApplyRemoteChangesAsync(store, response);

        Assert.False(File.Exists(filePath)); // 本地副本已删除
    }

    // T-037：远端新文件首次下载——树返回 Synced，快照创建但下载未完成，IsDownloaded 不得为 true
    [Fact]
    public async Task ApplyRemoteChanges_新远端文件_快照未标记已落盘()
    {
        var response = new FileTreeResponse(
            new[]
            {
                new FileEntryDto("/new-remote.bin", (int)CloudPan.Contract.FileType.File,
                    "hash", 100, 2, DateTime.UtcNow.ToString("O"), (int)CloudPan.Contract.FileState.Synced)
            },
            null, false, 2);

        await using var store = await _storeFactory.CreateStoreAsync();
        await CallApplyRemoteChangesAsync(store, response);
        await store.CommitAsync();

        var snapshot = await store.GetSnapshotAsync("/new-remote.bin");
        Assert.NotNull(snapshot);
        Assert.False(snapshot!.IsDownloaded); // 下载完成前不得标记为已落盘
        // 已入队下载
        Assert.NotNull(await store.GetQueueByPathAndOperationAsync("/new-remote.bin", (int)SyncOperation.Download));
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

    // T-045：冲突项不阻塞队列其他传输——5 个待解决冲突 + 1 上传，上传仍被处理（不再整体饥饿）
    [Fact]
    public async Task ProcessQueue_待解决冲突5个_上传不被饥饿()
    {
        // 5 个待决策冲突路径写入 _pendingConflicts（模拟 409 后等待用户决策的状态）
        string[] conflictPaths = Enumerable.Range(0, 5).Select(i => $"/conflict-{i}.txt").ToArray();
        var pendingField = typeof(SyncEngine).GetField("_pendingConflicts",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(pendingField);
        var pending = (System.Collections.Concurrent.ConcurrentDictionary<string, ConflictInfo>)pendingField!.GetValue(_engine)!;
        foreach (string p in conflictPaths)
        {
            pending.TryAdd(p, new ConflictInfo(p, Path.Combine(_syncRoot, p.TrimStart('/')), DateTime.UtcNow, null, 10, 10, "hash"));
        }

        // 队列预置：5 个冲突项（高优先级 + 最早 CreatedAt，修复前恒占前 5 槽位）+ 后续真实上传
        await using (var setupDb = await _dbFactory.CreateDbContextAsync())
        {
            foreach (string p in conflictPaths)
            {
                setupDb.SyncQueue.Add(new SyncQueue
                {
                    FilePath = p,
                    Operation = (int)SyncOperation.Upload,
                    Priority = (int)QueuePriority.High,
                    CreatedAt = "2026-01-01T00:00:00.0000000Z" // 最早的 CreatedAt → 排序时排最前
                });
            }
            await setupDb.SaveChangesAsync();
        }

        string uploadPath = Path.Combine(_syncRoot, "not-starved.txt");
        await File.WriteAllTextAsync(uploadPath, "必须被处理");
        await _engine.EnqueueLocalChangeAsync("/not-starved.txt", SyncOperation.Upload);

        // 处理队列
        var method = typeof(SyncEngine).GetMethod("ProcessQueueAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(method);
        Task? task = (Task?)method!.Invoke(_engine, [CancellationToken.None]);
        Assert.NotNull(task);
        await task;

        // 上传未被饥饿：已调用上传 API 且队列项已处理移除
        Assert.True(_api.UploadCalls.ContainsKey("/not-starved.txt"));
        await using var dbFinal = await _dbFactory.CreateDbContextAsync();
        Assert.Equal(0, await dbFinal.SyncQueue.CountAsync(q => q.FilePath == "/not-starved.txt"));

        // 冲突项仍保留在 DB（等待用户解决后由 OnConflictResolved 清除）
        Assert.Equal(5, await dbFinal.SyncQueue.CountAsync(q => conflictPaths.Contains(q.FilePath)));
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

    // T-036：409 冲突的 RemoteModifiedTime 从 /api/tree 快照的 LastModified 取真实时间（不再恒 null）
    [Fact]
    public async Task ProcessUpload_双设备并发编辑409_冲突信息带真实远程修改时间()
    {
        string filePath = Path.Combine(_syncRoot, "remote-mod.txt");
        await File.WriteAllTextAsync(filePath, "本机编辑内容");

        // 设备 A 两次上传（服务端 v1 → v2）；设备 B 的快照停留在 v1，含 /api/tree 的 lastModified
        await _api.UploadAsync(filePath, "/remote-mod.txt", baseVersion: 0, lastModified: DateTime.UtcNow.ToString("O"));
        await _api.UploadAsync(filePath, "/remote-mod.txt", baseVersion: 1, lastModified: DateTime.UtcNow.ToString("O"));

        DateTime remoteModifiedUtc = new DateTime(2026, 8, 2, 9, 30, 0, DateTimeKind.Utc);
        await using (var setupDb = await _dbFactory.CreateDbContextAsync())
        {
            setupDb.RemoteSnapshots.Add(new RemoteSnapshot
            {
                Path = "/remote-mod.txt", Type = 0, Size = new FileInfo(filePath).Length,
                Version = 1, State = 0, Hash = "hash-v1",
                LastModified = remoteModifiedUtc.ToString("O") // T-036：快照记录远程真实修改时间
            });
            await setupDb.SaveChangesAsync();
        }

        await _engine.EnqueueLocalChangeAsync("/remote-mod.txt", SyncOperation.Upload);

        ConflictInfo? conflictInfo = null;
        _conflictHandler = ci => { conflictInfo = ci; };
        _engine.ConflictDetected += _conflictHandler;
        try
        {
            var method = typeof(SyncEngine).GetMethod("ProcessQueueAsync",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.NotNull(method);
            Task? task = (Task?)method!.Invoke(_engine, [CancellationToken.None]);
            Assert.NotNull(task);
            await task;
        }
        finally
        {
            _engine.ConflictDetected -= _conflictHandler;
            _conflictHandler = null;
        }

        Assert.NotNull(conflictInfo);
        Assert.Equal("/remote-mod.txt", conflictInfo!.RelativePath);
        // 远程版本面板展示真实修改时间（本地时区），不再是「未知」
        Assert.NotNull(conflictInfo.RemoteModifiedTime);
        Assert.Equal(remoteModifiedUtc, conflictInfo.RemoteModifiedTime!.Value.ToUniversalTime());
    }

    // T-084：删除 409 —— 文件被其他设备修改/上传（服务端版本 > 本机快照版本），
    // 转入冲突流程（ConflictDetected + _pendingConflicts）而非静默丢弃删除意图
    [Fact]
    public async Task ProcessDelete_服务端409_转入冲突流程不静默丢弃()
    {
        // 服务端 v2（另一设备已修改/上传），本机快照停留在 v1，本地文件存在（删除意图真实）
        string filePath = Path.Combine(_syncRoot, "del-conflict.txt");
        await File.WriteAllTextAsync(filePath, "本地待删内容");
        _api.Files["/del-conflict.txt"] = ("hash-v2", 8, 2);

        await using (var setupDb = await _dbFactory.CreateDbContextAsync())
        {
            setupDb.RemoteSnapshots.Add(new RemoteSnapshot
            {
                Path = "/del-conflict.txt", Type = (int)FileType.File,
                Size = new FileInfo(filePath).Length, Version = 1,
                State = (int)FileState.Synced, Hash = "hash-v1"
            });
            await setupDb.SaveChangesAsync();
        }

        // 本地删除 → 入队删除（T-084：携带 BaseVersion = 快照版本 1，服务端据此检测并发修改）
        await _engine.EnqueueLocalChangeAsync("/del-conflict.txt", SyncOperation.Delete);

        await using (var enqueueDb = await _dbFactory.CreateDbContextAsync())
        {
            var item = await enqueueDb.SyncQueue.FirstOrDefaultAsync(q => q.FilePath == "/del-conflict.txt");
            Assert.NotNull(item);
            Assert.Equal(1, item!.BaseVersion);
        }

        // 订阅冲突事件（具名字段处理器，Dispose 中退订满足 CP300）
        int conflictCount = 0;
        ConflictInfo? conflictInfo = null;
        _conflictHandler = ci => { conflictCount++; conflictInfo = ci; };
        _engine.ConflictDetected += _conflictHandler;

        // 处理队列 → 服务端 409 → 转入冲突流程
        var method = typeof(SyncEngine).GetMethod("ProcessQueueAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(method);
        Task? task = (Task?)method!.Invoke(_engine, [CancellationToken.None]);
        Assert.NotNull(task);
        await task;

        Assert.Equal(1, conflictCount); // 冲突提示已触发，未静默丢弃
        Assert.NotNull(conflictInfo);
        Assert.Equal("/del-conflict.txt", conflictInfo!.RelativePath);

        // 本地副本与快照均保留（删除未生效，等待用户决策），队列项保留（被 _pendingConflicts 跳过）
        Assert.True(File.Exists(filePath));
        await using (var dbFinal = await _dbFactory.CreateDbContextAsync())
        {
            Assert.NotNull(await dbFinal.RemoteSnapshots.FindAsync("/del-conflict.txt"));
            int remaining = await dbFinal.SyncQueue.CountAsync(q => q.FilePath == "/del-conflict.txt");
            Assert.Equal(1, remaining);
        }
    }

    // T-098：删除冲突『仍删除（强制）』——用户从冲突对话框选择 ForceDelete，
    // 以 baseVersion=0 入队强制删除：服务端直接删（不校验版本）、本地删除、快照清理。
    [Fact]
    public async Task ProcessDelete_服务端409_选择仍删除强制_以baseVersion0强制删除()
    {
        // 服务端 v2（另一设备已修改），本机快照 v1，本地文件存在（删除意图真实）
        string filePath = Path.Combine(_syncRoot, "del-force.txt");
        await File.WriteAllTextAsync(filePath, "本地待删内容");
        _api.Files["/del-force.txt"] = ("hash-v2", 8, 2);

        await using (var setupDb = await _dbFactory.CreateDbContextAsync())
        {
            setupDb.RemoteSnapshots.Add(new RemoteSnapshot
            {
                Path = "/del-force.txt", Type = (int)FileType.File,
                Size = new FileInfo(filePath).Length, Version = 1,
                State = (int)FileState.Synced, Hash = "hash-v1"
            });
            await setupDb.SaveChangesAsync();
        }

        await _engine.EnqueueLocalChangeAsync("/del-force.txt", SyncOperation.Delete);

        // 第一次处理队列 → 服务端 409 → 转入冲突流程（删除未生效）
        await InvokeProcessQueueAsync();
        Assert.True(File.Exists(filePath));

        // 用户选择『仍删除（强制）』
        await _engine.OnConflictResolved("/del-force.txt", ConflictResolution.ForceDelete);

        // 新队列项为 Delete + BaseVersion=0（强制，不校验版本）
        await using (var dbAfterResolve = await _dbFactory.CreateDbContextAsync())
        {
            var item = await dbAfterResolve.SyncQueue.FirstOrDefaultAsync(q => q.FilePath == "/del-force.txt");
            Assert.NotNull(item);
            Assert.Equal((int)SyncOperation.Delete, item!.Operation);
            Assert.Equal(0, item.BaseVersion);
        }

        // 第二次处理队列 → baseVersion=0 → 服务端直接删除 + 本地删除 + 快照清理
        await InvokeProcessQueueAsync();

        Assert.False(_api.Files.ContainsKey("/del-force.txt")); // 服务端已删
        Assert.False(File.Exists(filePath)); // 本地已删
        await using (var dbFinal = await _dbFactory.CreateDbContextAsync())
        {
            Assert.Null(await dbFinal.RemoteSnapshots.FindAsync("/del-force.txt")); // 快照已清理
            Assert.Equal(0, await dbFinal.SyncQueue.CountAsync(q => q.FilePath == "/del-force.txt")); // 队列已空
        }
    }

    // T-098：删除冲突远程版本信息从本地快照填充——RemoteHash/RemoteSize/RemoteModifiedTime
    // 为真实值而非『未知』（对齐上传冲突 409 语义），冲突对话框云盘版本面板展示真实值。
    [Fact]
    public async Task ProcessDelete_服务端409_冲突信息带真实远程版本()
    {
        string filePath = Path.Combine(_syncRoot, "del-remote.txt");
        await File.WriteAllTextAsync(filePath, "本地待删内容");
        _api.Files["/del-remote.txt"] = ("hash-v2", 8, 2);

        DateTime remoteModifiedUtc = new DateTime(2026, 8, 2, 9, 30, 0, DateTimeKind.Utc);
        await using (var setupDb = await _dbFactory.CreateDbContextAsync())
        {
            setupDb.RemoteSnapshots.Add(new RemoteSnapshot
            {
                Path = "/del-remote.txt", Type = (int)FileType.File,
                Size = new FileInfo(filePath).Length, Version = 1,
                State = (int)FileState.Synced, Hash = "hash-v1",
                LastModified = remoteModifiedUtc.ToString("O") // 快照记录远程真实修改时间
            });
            await setupDb.SaveChangesAsync();
        }

        await _engine.EnqueueLocalChangeAsync("/del-remote.txt", SyncOperation.Delete);

        ConflictInfo? conflictInfo = null;
        _conflictHandler = ci => { conflictInfo = ci; };
        _engine.ConflictDetected += _conflictHandler;
        try
        {
            await InvokeProcessQueueAsync();
        }
        finally
        {
            _engine.ConflictDetected -= _conflictHandler;
            _conflictHandler = null;
        }

        Assert.NotNull(conflictInfo);
        Assert.Equal(SyncOperation.Delete, conflictInfo!.Operation); // 删除冲突标记（UI 识别依据）
        Assert.Equal("hash-v1", conflictInfo.RemoteHash); // 远程哈希来自快照，非「未知」
        Assert.Equal(new FileInfo(filePath).Length, conflictInfo.RemoteFileSize);
        Assert.NotNull(conflictInfo.RemoteModifiedTime);
        Assert.Equal(remoteModifiedUtc, conflictInfo.RemoteModifiedTime!.Value.ToUniversalTime());
    }

    // T-098：删除冲突本地文件缺失容错——409 到达前本地文件已被其他路径删除（双删/重命名竞态），
    // FileNotFoundException 不逃逸到 ProcessQueueAsync 泛化 catch：跳过冲突入列、
    // 按服务端 409 白话提示返回处理结果、不抛异常（CLAUDE.md 7.3 异常恢复路径）。
    [Fact]
    public async Task ProcessDelete_服务端409_本地文件缺失_不抛异常给白话提示()
    {
        string filePath = Path.Combine(_syncRoot, "del-gone.txt");
        await File.WriteAllTextAsync(filePath, "将被删除");
        _api.Files["/del-gone.txt"] = ("hash-v2", 8, 2);

        await using (var setupDb = await _dbFactory.CreateDbContextAsync())
        {
            setupDb.RemoteSnapshots.Add(new RemoteSnapshot
            {
                Path = "/del-gone.txt", Type = (int)FileType.File,
                Size = new FileInfo(filePath).Length, Version = 1,
                State = (int)FileState.Synced, Hash = "hash-v1"
            });
            await setupDb.SaveChangesAsync();
        }

        await _engine.EnqueueLocalChangeAsync("/del-gone.txt", SyncOperation.Delete);

        // 模拟 409 到达前本地文件被其他路径删除/重命名（双删/重命名竞态）
        File.Delete(filePath);

        int conflictCount = 0;
        ErrorAttribution? attribution = null;
        SyncOperation? errOp = null;
        _conflictHandler = _ => conflictCount++;
        _engine.ConflictDetected += _conflictHandler;
        _errorHandler = (p, a, o) => { attribution = a; errOp = o; };
        _engine.ErrorOccurred += _errorHandler;
        try
        {
            await InvokeProcessQueueAsync(); // 不抛异常（FileNotFoundException 被捕获）
        }
        finally
        {
            _engine.ConflictDetected -= _conflictHandler;
            _conflictHandler = null;
            _engine.ErrorOccurred -= _errorHandler;
            _errorHandler = null;
        }

        Assert.Equal(0, conflictCount); // 跳过冲突入列（本地文件缺失，无本地版本可展示）
        Assert.NotNull(attribution); // 服务端 409 白话提示已触发
        Assert.Equal(SyncOperation.Delete, errOp);
        // 队列项处理完成（返回 true 移除），不再死循环重试
        await using (var dbFinal = await _dbFactory.CreateDbContextAsync())
        {
            Assert.Equal(0, await dbFinal.SyncQueue.CountAsync(q => q.FilePath == "/del-gone.txt"));
        }
    }

    // ============================================================
    // 连续 401 触发重配引导测试（F-34/T-034）
    // ============================================================

    [Fact]
    public async Task 持续401_达到阈值_触发重配引导一次()
    {
        // 三个待上传文件：上传全部返回 401 → 连续认证失败达到阈值 → ReconfigurationRequired 触发一次
        _api.AuthFailMode = true;
        for (int i = 0; i < 3; i++)
        {
            string name = $"auth-{i}.txt";
            await File.WriteAllTextAsync(Path.Combine(_syncRoot, name), "content");
            await _engine.EnqueueLocalChangeAsync($"/{name}", SyncOperation.Upload);
        }

        int reconfigCount = 0;
        _reconfigHandler = () => reconfigCount++;
        _engine.ReconfigurationRequired += _reconfigHandler;

        var method = typeof(SyncEngine).GetMethod("ProcessQueueAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(method);
        Task? task = (Task?)method!.Invoke(_engine, [CancellationToken.None]);
        Assert.NotNull(task);
        await task;

        // 恰好越过阈值触发一次；计数继续增长不再重复触发
        Assert.Equal(1, reconfigCount);
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
            setupDb.SyncQueue.Add(new SyncQueue
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

    // ============================================================
    // GetFileBrowserAsync 文件浏览测试（T-013）
    // ============================================================

    [Fact]
    public async Task GetFileBrowser_根目录_返回直接子项()
    {
        // 服务端快照：/photos 目录（CloudOnly）+ /photos/summer.jpg（CloudOnly，深层）+ /readme.md（Synced）
        await using (var setupDb = await _dbFactory.CreateDbContextAsync())
        {
            setupDb.RemoteSnapshots.AddRange(
                new RemoteSnapshot { Path = "/photos", Type = (int)FileType.Directory, State = (int)FileState.CloudOnly, Version = 1 },
                new RemoteSnapshot { Path = "/photos/summer.jpg", Type = (int)FileType.File, State = (int)FileState.CloudOnly, Version = 1 },
                new RemoteSnapshot { Path = "/readme.md", Type = (int)FileType.File, State = (int)FileState.Synced, Version = 1 });
            await setupDb.SaveChangesAsync();
        }

        // 本地新增文件（快照无 → Modified）
        await File.WriteAllTextAsync(Path.Combine(_syncRoot, "mylocal.txt"), "new local");

        var items = await _engine.GetFileBrowserAsync("/");

        // 目录优先：/photos 在最前
        Assert.Equal("/photos", items[0].Path);
        Assert.True(items[0].IsDirectory);

        // 目录模式不返回深层子项
        Assert.DoesNotContain(items, i => i.Path == "/photos/summer.jpg");

        // 本地新文件并入（Modified，本地存在，大小可读）
        var local = items.Single(i => i.Path == "/mylocal.txt");
        Assert.False(local.IsDirectory);
        Assert.Equal((int)FileState.Modified, local.State);
        Assert.True(local.LocalExists);
        Assert.True(local.Size > 0);

        // 快照文件包含在根目录
        Assert.Contains(items, i => i.Path == "/readme.md");
        Assert.Equal(3, items.Count);
    }

    [Fact]
    public async Task GetFileBrowser_子目录_返回直接子项()
    {
        await using (var setupDb = await _dbFactory.CreateDbContextAsync())
        {
            setupDb.RemoteSnapshots.AddRange(
                new RemoteSnapshot { Path = "/photos", Type = (int)FileType.Directory, State = (int)FileState.CloudOnly, Version = 1 },
                new RemoteSnapshot { Path = "/photos/summer.jpg", Type = (int)FileType.File, State = (int)FileState.CloudOnly, Version = 1 },
                new RemoteSnapshot { Path = "/photos/sub/video.mp4", Type = (int)FileType.File, State = (int)FileState.CloudOnly, Version = 1 });
            await setupDb.SaveChangesAsync();
        }

        var items = await _engine.GetFileBrowserAsync("/photos");

        // 仅 /photos 的直接子项（排除自身与深层）
        Assert.Single(items);
        Assert.Equal("/photos/summer.jpg", items[0].Path);
    }

    [Fact]
    public async Task GetFileBrowser_搜索_递归命中文件()
    {
        await using (var setupDb = await _dbFactory.CreateDbContextAsync())
        {
            setupDb.RemoteSnapshots.AddRange(
                new RemoteSnapshot { Path = "/photos/summer.jpg", Type = (int)FileType.File, State = (int)FileState.CloudOnly, Version = 1 },
                new RemoteSnapshot { Path = "/docs/report.docx", Type = (int)FileType.File, State = (int)FileState.Synced, Version = 1 });
            await setupDb.SaveChangesAsync();
        }

        var items = await _engine.GetFileBrowserAsync("/", "summer");

        // 搜索递归命中深层文件，且仅名称匹配项
        Assert.Single(items);
        Assert.Equal("/photos/summer.jpg", items[0].Path);
    }

    [Fact]
    public async Task GetFileBrowser_墓碑Deleting_不展示()
    {
        await using (var setupDb = await _dbFactory.CreateDbContextAsync())
        {
            setupDb.RemoteSnapshots.AddRange(
                new RemoteSnapshot { Path = "/gone.txt", Type = (int)FileType.File, State = (int)FileState.Deleting, Version = 2 },
                new RemoteSnapshot { Path = "/alive.txt", Type = (int)FileType.File, State = (int)FileState.Synced, Version = 1 });
            await setupDb.SaveChangesAsync();
        }

        var items = await _engine.GetFileBrowserAsync("/");

        Assert.DoesNotContain(items, i => i.Path == "/gone.txt");
        Assert.Contains(items, i => i.Path == "/alive.txt");
    }

    // ============================================================
    // 回收站/删除进回收站（T-014）
    // ============================================================

    [Fact]
    public async Task DeleteForTrash_服务端有快照_删除进回收站并返回撤销条目()
    {
        // 准备：本地文件 + 服务端快照 + mock 服务端文件
        string localPath = Path.Combine(_syncRoot, "photo.jpg");
        await File.WriteAllTextAsync(localPath, "jpeg-data");
        _api.Files["/photo.jpg"] = ("mock-hash", 9, 5);
        await using (var setupDb = await _dbFactory.CreateDbContextAsync())
        {
            setupDb.RemoteSnapshots.Add(new RemoteSnapshot
            {
                Path = "/photo.jpg", Type = (int)FileType.File,
                Hash = "mock-hash", Size = 9, Version = 5, State = (int)FileState.Synced
            });
            await setupDb.SaveChangesAsync();
        }

        var trashItem = await _engine.DeleteForTrashAsync("/photo.jpg");

        // 本地副本已删
        Assert.False(File.Exists(localPath));
        // 服务端已删（移入回收站）
        Assert.False(_api.Files.ContainsKey("/photo.jpg"));
        Assert.Single(_api.TrashItems);
        // 快照已清
        await using (var dbCheck = await _dbFactory.CreateDbContextAsync())
        {
            Assert.Null(await dbCheck.RemoteSnapshots.FindAsync("/photo.jpg"));
        }
        // 返回可撤销条目
        Assert.NotNull(trashItem);
        Assert.Equal("/photo.jpg", trashItem.OriginalPath);
    }

    [Fact]
    public async Task DeleteForTrash_本地仅存文件_直接删本地无撤销条目()
    {
        string localPath = Path.Combine(_syncRoot, "local-only.txt");
        await File.WriteAllTextAsync(localPath, "x");

        var trashItem = await _engine.DeleteForTrashAsync("/local-only.txt");

        Assert.False(File.Exists(localPath));
        Assert.Null(trashItem);          // 无服务端记录，无从回收站撤销（=成功）
        Assert.Empty(_api.TrashItems);   // 未进回收站
    }

    [Fact]
    public async Task DeleteForTrash_服务端删除失败_抛异常而非返回null()
    {
        // T-115：服务端删除失败须抛出异常（删除未生效，本地副本保留），不得返回 null 伪装成功——
        // 否则 UI 一律记『已删除』、失败不计数、不弹「成功 N / 失败 M」汇总。
        string localPath = Path.Combine(_syncRoot, "photo.jpg");
        await File.WriteAllTextAsync(localPath, "jpeg-data");
        _api.Files["/photo.jpg"] = ("mock-hash", 9, 6); // 服务端当前版本已高于客户端快照 v5
        await using (var setupDb = await _dbFactory.CreateDbContextAsync())
        {
            setupDb.RemoteSnapshots.Add(new RemoteSnapshot
            {
                Path = "/photo.jpg", Type = (int)FileType.File,
                Hash = "mock-hash", Size = 9, Version = 5, State = (int)FileState.Synced
            });
            await setupDb.SaveChangesAsync();
        }

        // 服务端删除失败（409 版本冲突，对齐 MockApiClient.DeleteAsync 语义）→ 抛异常
        await Assert.ThrowsAsync<System.Net.Http.HttpRequestException>(
            () => _engine.DeleteForTrashAsync("/photo.jpg"));

        // 本地副本保留（删除未生效）
        Assert.True(File.Exists(localPath));
        // 服务端文件保留、未进回收站
        Assert.True(_api.Files.ContainsKey("/photo.jpg"));
        Assert.Empty(_api.TrashItems);
        // 快照保留（未清）
        await using (var dbCheck = await _dbFactory.CreateDbContextAsync())
        {
            Assert.NotNull(await dbCheck.RemoteSnapshots.FindAsync("/photo.jpg"));
        }
    }

    [Fact]
    public async Task DeleteForTrash_目录_清子路径快照()
    {
        // 本地目录 + 子文件 + 快照（目录与子文件）
        string dirPath = Path.Combine(_syncRoot, "photos");
        Directory.CreateDirectory(dirPath);
        await File.WriteAllTextAsync(Path.Combine(dirPath, "summer.jpg"), "jpeg");
        _api.Files["/photos/"] = (null!, 0, 4);
        _api.Files["/photos/summer.jpg"] = ("mock-hash", 4, 4);
        await using (var setupDb = await _dbFactory.CreateDbContextAsync())
        {
            setupDb.RemoteSnapshots.AddRange(
                new RemoteSnapshot { Path = "/photos", Type = (int)FileType.Directory, State = (int)FileState.Synced, Version = 4 },
                new RemoteSnapshot { Path = "/photos/summer.jpg", Type = (int)FileType.File, State = (int)FileState.Synced, Version = 4 });
            await setupDb.SaveChangesAsync();
        }

        var trashItem = await _engine.DeleteForTrashAsync("/photos");

        Assert.False(Directory.Exists(dirPath));
        Assert.NotNull(trashItem);
        Assert.True(trashItem.IsDirectory);
        await using (var dbCheck = await _dbFactory.CreateDbContextAsync())
        {
            Assert.Null(await dbCheck.RemoteSnapshots.FindAsync("/photos"));
            Assert.Null(await dbCheck.RemoteSnapshots.FindAsync("/photos/summer.jpg")); // 子快照已清
        }
    }

    [Fact]
    public async Task RestoreTrash_恢复后回到服务端()
    {
        // 先删除一个文件（进回收站）
        string localPath = Path.Combine(_syncRoot, "doc.txt");
        await File.WriteAllTextAsync(localPath, "doc-content");
        _api.Files["/doc.txt"] = ("mock-hash", 10, 3);
        await using (var setupDb = await _dbFactory.CreateDbContextAsync())
        {
            setupDb.RemoteSnapshots.Add(new RemoteSnapshot
            {
                Path = "/doc.txt", Type = (int)FileType.File,
                Hash = "mock-hash", Size = 10, Version = 3, State = (int)FileState.Synced
            });
            await setupDb.SaveChangesAsync();
        }
        var trashItem = await _engine.DeleteForTrashAsync("/doc.txt");
        Assert.NotNull(trashItem);

        bool ok = await _engine.RestoreTrashAsync(trashItem!);

        Assert.True(ok);
        Assert.True(_api.Files.ContainsKey("/doc.txt")); // 服务端已恢复
        Assert.Empty(_api.TrashItems);                    // 回收站条目已移除
    }

    [Fact]
    public async Task GetTrash_返回回收站列表()
    {
        _api.TrashItems.Add(new TrashItem("/old.txt", "mock_abc123", 5, false, DateTime.UtcNow.ToString("O"), 0));

        var items = await _engine.GetTrashAsync();

        Assert.Single(items);
        Assert.Equal("/old.txt", items[0].OriginalPath);
    }

    [Fact]
    public async Task EmptyTrash_清空回收站()
    {
        _api.TrashItems.Add(new TrashItem("/old.txt", "mock_abc123", 5, false, DateTime.UtcNow.ToString("O"), 0));

        bool ok = await _engine.EmptyTrashAsync();

        Assert.True(ok);
        Assert.Empty(_api.TrashItems);
    }

    // ============================================================
    // 分享 + 版本历史转发测试（T-018）
    // ============================================================

    [Fact]
    public async Task CreateShare_成功_返回链接与分享ID()
    {
        _api.Files["/share-me.txt"] = ("mock-hash", 10, 1);

        var result = await _engine.CreateShareAsync("/share-me.txt", "1234", "2026-08-10T00:00:00.0000000Z", null);

        Assert.NotNull(result?.Data);
        Assert.False(string.IsNullOrEmpty(result.Data.ShareId));
        Assert.Equal("http://localhost:8443/share/" + result.Data.ShareId, result.Data.Url);
        Assert.Equal("2026-08-10T00:00:00.0000000Z", result.Data.ExpiresAt);
        Assert.True(_api.Shares.ContainsKey(result.Data.ShareId)); // 已写入 mock 服务端
    }

    [Fact]
    public async Task CreateShare_文件不存在_返回null()
    {
        // 文件不存在 → mock 抛 404 → SyncEngine 容错返回 null（不抛给 UI）
        var result = await _engine.CreateShareAsync("/no-such.txt", null, null, null);

        Assert.Null(result);
        Assert.Empty(_api.Shares);
    }

    [Fact]
    public async Task RevokeShare_撤销成功_返回true()
    {
        string shareId = "mockshare0001";
        _api.Shares[shareId] = new ShareCreateData(shareId, "http://localhost:8443/share/" + shareId, null, null);

        bool ok = await _engine.RevokeShareAsync(shareId);

        Assert.True(ok);
        Assert.Empty(_api.Shares);
    }

    [Fact]
    public async Task RevokeShare_分享不存在_返回false()
    {
        bool ok = await _engine.RevokeShareAsync("no-such-share");

        Assert.False(ok);
    }

    [Fact]
    public async Task GetVersionHistory_返回版本列表()
    {
        _api.VersionHistory["/photo.jpg"] = new List<VersionItem>
        {
            new VersionItem(3, "hash3", 30, "2026-08-03T01:00:00.0000000Z", "dev-a", null),
            new VersionItem(2, "hash2", 20, "2026-08-02T01:00:00.0000000Z", "dev-b", null)
        };

        var versions = await _engine.GetVersionHistoryAsync("/photo.jpg");

        Assert.Equal(2, versions.Count);
        Assert.Equal(3, versions[0].Version);
        Assert.Equal("dev-a", versions[0].DeviceId);
    }

    [Fact]
    public async Task RestoreVersion_回滚成功_返回新版本号()
    {
        _api.VersionHistory["/photo.jpg"] = new List<VersionItem>
        {
            new VersionItem(3, "hash3", 30, "2026-08-03T01:00:00.0000000Z", "dev-a", null)
        };

        var result = await _engine.RestoreVersionAsync("/photo.jpg", 3);

        Assert.NotNull(result?.Data);
        Assert.Equal("/photo.jpg", result.Data.Path);
        Assert.Equal(4, result.Data.Version);       // 回滚后提升到新版本
        Assert.Equal(3, result.Data.RestoredFromVersion); // 记录回滚来源
    }

    [Fact]
    public async Task RestoreVersion_版本不存在_返回null()
    {
        _api.VersionHistory["/photo.jpg"] = new List<VersionItem>
        {
            new VersionItem(3, "hash3", 30, "2026-08-03T01:00:00.0000000Z", "dev-a", null)
        };

        var result = await _engine.RestoreVersionAsync("/photo.jpg", 99);

        Assert.Null(result);
    }

    // ============================================================
    // 上传入口 + CloudOnly 按需下载（T-033）
    // ============================================================

    [Fact]
    public async Task ImportFilesAsync_复制到同步目录_入队上传()
    {
        // 源文件在同步根之外（temp 目录），目标为同步根
        string source = Path.Combine(_tempDir, "import-src.txt");
        await File.WriteAllTextAsync(source, "import me");

        await _engine.ImportFilesAsync([source], "/");

        // 已复制到同步根
        Assert.True(File.Exists(Path.Combine(_syncRoot, "import-src.txt")));
        // 已入队上传
        await using var dbCheck = await _dbFactory.CreateDbContextAsync();
        var item = await dbCheck.SyncQueue.FirstOrDefaultAsync(q => q.FilePath == "/import-src.txt");
        Assert.NotNull(item);
        Assert.Equal((int)SyncOperation.Upload, item!.Operation);
    }

    [Fact]
    public async Task ImportFilesAsync_目标目录含上级跳转_拒绝导入()
    {
        string source = Path.Combine(_tempDir, "evil-src.txt");
        await File.WriteAllTextAsync(source, "evil");

        await _engine.ImportFilesAsync([source], "/../outside");

        // 未复制到同步根外、未入队上传
        Assert.False(File.Exists(Path.Combine(_tempDir, "outside", "evil-src.txt")));
        await using var dbCheck = await _dbFactory.CreateDbContextAsync();
        Assert.Equal(0, await dbCheck.SyncQueue.CountAsync());
    }

    [Fact]
    public async Task DownloadPathAsync_CloudOnly文件_入队下载()
    {
        // 预置 CloudOnly 快照（无本地副本）
        await using (var setupDb = await _dbFactory.CreateDbContextAsync())
        {
            setupDb.RemoteSnapshots.Add(new RemoteSnapshot
            {
                Path = "/cloud-dl.txt", Type = (int)FileType.File,
                Size = 12, Version = 3, State = (int)FileState.CloudOnly, Hash = "cloud-hash"
            });
            await setupDb.SaveChangesAsync();
        }

        await _engine.DownloadPathAsync("/cloud-dl.txt");

        await using var dbCheck = await _dbFactory.CreateDbContextAsync();
        var item = await dbCheck.SyncQueue.FirstOrDefaultAsync(q => q.FilePath == "/cloud-dl.txt");
        Assert.NotNull(item);
        Assert.Equal((int)SyncOperation.Download, item!.Operation);
        Assert.Equal((int)QueuePriority.High, item.Priority);
    }

    [Fact]
    public async Task DownloadPathAsync_处理队列_下载到本地并转Synced()
    {
        // 预置 CloudOnly 快照（无本地副本）
        await using (var setupDb = await _dbFactory.CreateDbContextAsync())
        {
            setupDb.RemoteSnapshots.Add(new RemoteSnapshot
            {
                Path = "/cloud-dl.txt", Type = (int)FileType.File,
                Size = 12, Version = 3, State = (int)FileState.CloudOnly, Hash = "cloud-hash"
            });
            await setupDb.SaveChangesAsync();
        }

        await _engine.DownloadPathAsync("/cloud-dl.txt");

        // 处理队列 → 下载完成 → 本地副本生成
        var method = typeof(SyncEngine).GetMethod("ProcessQueueAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(method);
        Task? task = (Task?)method!.Invoke(_engine, [CancellationToken.None]);
        Assert.NotNull(task);
        await task;

        // 本地副本已生成
        Assert.True(File.Exists(Path.Combine(_syncRoot, "cloud-dl.txt")));
        Assert.True(_api.DownloadCalls.ContainsKey("/cloud-dl.txt"));

        // 快照转 Synced
        await using var dbFinal = await _dbFactory.CreateDbContextAsync();
        var snapshot = await dbFinal.RemoteSnapshots.FindAsync("/cloud-dl.txt");
        Assert.NotNull(snapshot);
        Assert.Equal((int)FileState.Synced, snapshot!.State);
        Assert.True(snapshot.IsDownloaded); // T-037：下载完成后标记已落盘，删除判定恢复

        // 队列已清空
        Assert.Equal(0, await dbFinal.SyncQueue.CountAsync());
    }

    // ============================================================
    // 目录同步为服务端条目（T-046）
    // ============================================================

    [Fact]
    public async Task FullScan_新建目录_入队mkdir并同步为服务端条目()
    {
        // 本地新建空目录（不含快照）——修复前被 File.Exists 丢弃，服务端无条目
        Directory.CreateDirectory(Path.Combine(_syncRoot, "photos"));

        await _engine.FullScanAsync();

        // 已入队 Upload（mkdir 语义）
        await using (var dbCheck = await _dbFactory.CreateDbContextAsync())
        {
            var item = await dbCheck.SyncQueue.FirstOrDefaultAsync(q => q.FilePath == "/photos");
            Assert.NotNull(item);
            Assert.Equal((int)SyncOperation.Upload, item!.Operation);
        }

        // 处理队列 → ProcessUploadAsync 对目录调 MkdirAsync → 服务端条目 + 目录快照
        var method = typeof(SyncEngine).GetMethod("ProcessQueueAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(method);
        Task? task = (Task?)method!.Invoke(_engine, [CancellationToken.None]);
        Assert.NotNull(task);
        await task;

        Assert.True(_api.MkdirCalls.ContainsKey("/photos")); // 已调服务端 Mkdir
        await using var dbFinal = await _dbFactory.CreateDbContextAsync();
        var snapshot = await dbFinal.RemoteSnapshots.FindAsync("/photos");
        Assert.NotNull(snapshot);
        Assert.Equal((int)FileType.Directory, snapshot!.Type);
        Assert.Equal((int)FileState.Synced, snapshot.State);
        Assert.Equal(0, await dbFinal.SyncQueue.CountAsync()); // 队列已清空
    }

    [Fact]
    public async Task ApplyRemoteChanges_远端目录_空目录快照并浏览可见()
    {
        // 另一设备在服务端创建了空目录 /photos（FileEntry 行，无尾斜杠路径）
        var response = new FileTreeResponse(
            new[]
            {
                new FileEntryDto("/photos", (int)FileType.Directory,
                    null, 0, 3, DateTime.UtcNow.ToString("O"), (int)FileState.Synced)
            },
            null, false, 3);

        await using var store = await _storeFactory.CreateStoreAsync();
        await CallApplyRemoteChangesAsync(store, response);
        await store.CommitAsync();

        // 快照已建（目录无落盘概念，视为已同步）
        var snapshot = await store.GetSnapshotAsync("/photos");
        Assert.NotNull(snapshot);
        Assert.Equal((int)FileType.Directory, snapshot!.Type);

        // 浏览视图可见——空目录在其他设备可见
        var items = await _engine.GetFileBrowserAsync("/");
        var dir = items.Single(i => i.Path == "/photos");
        Assert.True(dir.IsDirectory);
    }

    [Fact]
    public async Task ProcessRename_目录_快照收敛到新路径()
    {
        // 先同步本地目录 /photos（mkdir 建立服务端条目 + 目录快照）
        Directory.CreateDirectory(Path.Combine(_syncRoot, "photos"));
        await _engine.EnqueueLocalChangeAsync("/photos", SyncOperation.Upload);
        var method = typeof(SyncEngine).GetMethod("ProcessQueueAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(method);
        Task? task = (Task?)method!.Invoke(_engine, [CancellationToken.None]);
        Assert.NotNull(task);
        await task;
        await using (var setupDb = await _dbFactory.CreateDbContextAsync())
        {
            var snap = await setupDb.RemoteSnapshots.FindAsync("/photos");
            Assert.NotNull(snap);
            Assert.Equal((int)FileType.Directory, snap!.Type);
        }

        // 本地重命名 /photos → /vacation，经既有 Move 路径收敛（索引已补齐不再 404）
        Directory.Move(Path.Combine(_syncRoot, "photos"), Path.Combine(_syncRoot, "vacation"));
        await _engine.EnqueueRenameAsync("/photos", "/vacation");

        task = (Task?)method!.Invoke(_engine, [CancellationToken.None]);
        Assert.NotNull(task);
        await task;

        // 已调服务端 Move，旧快照移除、新快照保留目录类型
        Assert.True(_api.MoveCalls.ContainsKey("/photos"));
        Assert.False(_api.Files.ContainsKey("/photos"));
        Assert.True(_api.Files.ContainsKey("/vacation")); // mock 服务端条目已移动
        await using var dbFinal = await _dbFactory.CreateDbContextAsync();
        Assert.Null(await dbFinal.RemoteSnapshots.FindAsync("/photos"));
        var moved = await dbFinal.RemoteSnapshots.FindAsync("/vacation");
        Assert.NotNull(moved);
        Assert.Equal((int)FileType.Directory, moved!.Type);
    }

    // ============================================================
    // 目录重命名快照前缀跟随（T-066）
    // ============================================================

    // T-066：重命名父目录时子项快照前缀跟随（旧前缀 → 新前缀，内容/版本/落盘标记保留），
    // 不触发整棵子树重下载/重上传/批量删除
    [Fact]
    public async Task ProcessRename_目录重命名_子项快照前缀跟随()
    {
        // 预置本地目录 /photos + 子文件 + 快照（目录与子项均曾落盘）
        string dirPath = Path.Combine(_syncRoot, "photos");
        Directory.CreateDirectory(Path.Combine(dirPath, "sub"));
        await File.WriteAllTextAsync(Path.Combine(dirPath, "summer.jpg"), "jpeg-data");
        await File.WriteAllTextAsync(Path.Combine(dirPath, "sub", "video.mp4"), "mp4-content");
        await using (var setupDb = await _dbFactory.CreateDbContextAsync())
        {
            setupDb.RemoteSnapshots.AddRange(
                new RemoteSnapshot { Path = "/photos", Type = (int)FileType.Directory, Size = 0, Version = 5, State = (int)FileState.Synced, IsDownloaded = true },
                new RemoteSnapshot { Path = "/photos/summer.jpg", Type = (int)FileType.File, Hash = "h1", Size = 9, Version = 5, State = (int)FileState.Synced, IsDownloaded = true },
                new RemoteSnapshot { Path = "/photos/sub/video.mp4", Type = (int)FileType.File, Hash = "h2", Size = 11, Version = 5, State = (int)FileState.Synced, IsDownloaded = true });
            await setupDb.SaveChangesAsync();
        }

        // 本地重命名 /photos → /vacation，入队并处理
        Directory.Move(dirPath, Path.Combine(_syncRoot, "vacation"));
        await _engine.EnqueueRenameAsync("/photos", "/vacation");
        var method = typeof(SyncEngine).GetMethod("ProcessQueueAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(method);
        Task? task = (Task?)method!.Invoke(_engine, [CancellationToken.None]);
        Assert.NotNull(task);
        await task;

        await using var dbCheck = await _dbFactory.CreateDbContextAsync();
        // 旧前缀快照全部移除（目录自身 + 子项）
        Assert.Null(await dbCheck.RemoteSnapshots.FindAsync("/photos"));
        Assert.Null(await dbCheck.RemoteSnapshots.FindAsync("/photos/summer.jpg"));
        Assert.Null(await dbCheck.RemoteSnapshots.FindAsync("/photos/sub/video.mp4"));
        // 子项快照已跟随到新前缀，字段（哈希/大小/版本/落盘）保留
        var movedFile = await dbCheck.RemoteSnapshots.FindAsync("/vacation/summer.jpg");
        Assert.NotNull(movedFile);
        Assert.Equal("h1", movedFile!.Hash);
        Assert.Equal(9, movedFile.Size);
        Assert.Equal(5, movedFile.Version);
        Assert.True(movedFile.IsDownloaded);
        var movedDeep = await dbCheck.RemoteSnapshots.FindAsync("/vacation/sub/video.mp4");
        Assert.NotNull(movedDeep);
        Assert.Equal("h2", movedDeep!.Hash);
        Assert.Equal(11, movedDeep.Size);
        var movedDir = await dbCheck.RemoteSnapshots.FindAsync("/vacation");
        Assert.NotNull(movedDir);
        Assert.Equal((int)FileType.Directory, movedDir!.Type);
        // 无整棵子树的重下载/重上传/删除队列项
        Assert.Equal(0, await dbCheck.SyncQueue.CountAsync());
    }

    // T-066：目录重命名处理时清空旧前缀下的未决队列项（watcher 残留 Delete/Upload），
    // 避免随后产生服务端 404 删除噪音
    [Fact]
    public async Task ProcessRename_目录重命名_清空旧前缀未决队列项()
    {
        // 预置快照 + 旧前缀下的未决队列项（FullScan 误判删除、watcher 残留上传）
        await using (var setupDb = await _dbFactory.CreateDbContextAsync())
        {
            setupDb.RemoteSnapshots.AddRange(
                new RemoteSnapshot { Path = "/photos", Type = (int)FileType.Directory, Version = 5, State = (int)FileState.Synced, IsDownloaded = true },
                new RemoteSnapshot { Path = "/photos/summer.jpg", Type = (int)FileType.File, Hash = "h1", Size = 9, Version = 5, State = (int)FileState.Synced, IsDownloaded = true });
            setupDb.SyncQueue.AddRange(
                new SyncQueue { FilePath = "/photos/summer.jpg", Operation = (int)SyncOperation.Delete, Priority = (int)QueuePriority.High },
                new SyncQueue { FilePath = "/photos/sub/video.mp4", Operation = (int)SyncOperation.Upload, Priority = (int)QueuePriority.High });
            await setupDb.SaveChangesAsync();
        }

        // 本地已重命名
        Directory.CreateDirectory(Path.Combine(_syncRoot, "photos"));
        Directory.Move(Path.Combine(_syncRoot, "photos"), Path.Combine(_syncRoot, "vacation"));

        // 反射直接调用 ProcessRenameAsync（不经队列排序，验证其清理逻辑本身）
        var method = typeof(SyncEngine).GetMethod("ProcessRenameAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(method);
        var item = new SyncQueue { FilePath = "/photos", TargetPath = "/vacation", Operation = (int)SyncOperation.Rename, BaseVersion = 5 };
        Task? task = (Task?)method!.Invoke(_engine, [item, CancellationToken.None]);
        Assert.NotNull(task);
        await task;

        await using var dbCheck = await _dbFactory.CreateDbContextAsync();
        // 旧前缀未决队列项已清空
        Assert.Equal(0, await dbCheck.SyncQueue.CountAsync());
        // 快照已跟随
        Assert.Null(await dbCheck.RemoteSnapshots.FindAsync("/photos/summer.jpg"));
        Assert.NotNull(await dbCheck.RemoteSnapshots.FindAsync("/vacation/summer.jpg"));
    }

    // T-066：目录重命名后子项快照已在新路径 → 远端树反映新路径时不再触发整棵子树重下载
    [Fact]
    public async Task ProcessRename_目录重命名后_远端新路径树不触发子项重下载()
    {
        // 先执行目录重命名（子项快照前缀跟随）
        string dirPath = Path.Combine(_syncRoot, "photos");
        Directory.CreateDirectory(Path.Combine(dirPath, "sub"));
        await File.WriteAllTextAsync(Path.Combine(dirPath, "summer.jpg"), "jpeg-data");
        await File.WriteAllTextAsync(Path.Combine(dirPath, "sub", "video.mp4"), "mp4-content");
        await using (var setupDb = await _dbFactory.CreateDbContextAsync())
        {
            setupDb.RemoteSnapshots.AddRange(
                new RemoteSnapshot { Path = "/photos", Type = (int)FileType.Directory, Size = 0, Version = 5, State = (int)FileState.Synced, IsDownloaded = true },
                new RemoteSnapshot { Path = "/photos/summer.jpg", Type = (int)FileType.File, Hash = "h1", Size = 9, Version = 5, State = (int)FileState.Synced, IsDownloaded = true },
                new RemoteSnapshot { Path = "/photos/sub/video.mp4", Type = (int)FileType.File, Hash = "h2", Size = 11, Version = 5, State = (int)FileState.Synced, IsDownloaded = true });
            await setupDb.SaveChangesAsync();
        }
        Directory.Move(dirPath, Path.Combine(_syncRoot, "vacation"));
        await _engine.EnqueueRenameAsync("/photos", "/vacation");
        var processMethod = typeof(SyncEngine).GetMethod("ProcessQueueAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(processMethod);
        Task? processTask = (Task?)processMethod!.Invoke(_engine, [CancellationToken.None]);
        Assert.NotNull(processTask);
        await processTask;

        // 远端树已反映新路径（服务端 Move 已完成），版本与快照相同 → 哈希相同跳过下载
        var response = new FileTreeResponse(
            new[]
            {
                new FileEntryDto("/vacation/summer.jpg", (int)FileType.File, "h1", 9, 5, DateTime.UtcNow.ToString("O"), (int)FileState.Synced),
                new FileEntryDto("/vacation/sub/video.mp4", (int)FileType.File, "h2", 11, 5, DateTime.UtcNow.ToString("O"), (int)FileState.Synced)
            },
            null, false, 5);

        await using var store = await _storeFactory.CreateStoreAsync();
        await CallApplyRemoteChangesAsync(store, response);
        await store.CommitAsync();

        // 子项快照版本相等、内容哈希相同 → 不入队下载（无整棵子树重下载）
        var allQueues = await store.GetAllQueuesAsync();
        Assert.Equal(0, allQueues.Count(q => q.Operation == (int)SyncOperation.Download));
    }

    // T-066：全量扫描落在重命名未决窗口（本地已改名、Move 未处理）时，不把 rename 判为
    // delete+create——旧前缀快照本地缺失不入队 Delete，新前缀本地文件不入队 Upload
    [Fact]
    public async Task FullScan_未决重命名_不入队删除与上传()
    {
        // 场景：目录本地已改名（/photos → /vacation），快照仍在旧前缀、本地文件在新前缀，Move 尚未处理
        string dirPath = Path.Combine(_syncRoot, "vacation");
        Directory.CreateDirectory(dirPath);
        await File.WriteAllTextAsync(Path.Combine(dirPath, "summer.jpg"), "jpeg-data");
        await using (var setupDb = await _dbFactory.CreateDbContextAsync())
        {
            setupDb.RemoteSnapshots.AddRange(
                new RemoteSnapshot { Path = "/photos", Type = (int)FileType.Directory, Size = 0, Version = 5, State = (int)FileState.Synced, IsDownloaded = true },
                new RemoteSnapshot { Path = "/photos/summer.jpg", Type = (int)FileType.File, Hash = "h1", Size = 9, Version = 5, State = (int)FileState.Synced, IsDownloaded = true });
            // 未决重命名：/photos → /vacation
            setupDb.SyncQueue.Add(new SyncQueue
            {
                FilePath = "/photos", Operation = (int)SyncOperation.Rename, TargetPath = "/vacation",
                Priority = (int)QueuePriority.High
            });
            await setupDb.SaveChangesAsync();
        }

        await _engine.FullScanAsync();

        await using var dbCheck = await _dbFactory.CreateDbContextAsync();
        // 旧前缀快照本地缺失 → 不入队 Delete（避免 Delete 先于 Move 到达制造回收站误删竞态 + 404 噪音）
        Assert.Equal(0, await dbCheck.SyncQueue.CountAsync(q => q.Operation == (int)SyncOperation.Delete));
        // 新前缀本地文件/目录无快照 → 不入队 Upload（避免 rename 判为 create 整棵子树重复上传）
        Assert.Equal(0, await dbCheck.SyncQueue.CountAsync(q => q.Operation == (int)SyncOperation.Upload));
        // 未决重命名项保留（待队列处理）
        Assert.Equal(1, await dbCheck.SyncQueue.CountAsync(q => q.Operation == (int)SyncOperation.Rename));
    }

    // ============================================================
    // 选择性同步排除集语义（T-047）
    // ============================================================

    /// <summary>反射获取 SyncEngine._paths（T-099 拆分：排除集/忽略规则判定移至 SyncPathSelector）。</summary>
    private object GetPathSelector(SyncEngine engine)
    {
        var field = typeof(SyncEngine).GetField("_paths",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(field);
        return field!.GetValue(engine)!;
    }

    /// <summary>反射调用 SyncRemoteApplier.ApplyRemoteChangesAsync（T-099 拆分后该方法下沉至 SyncRemoteApplier）。</summary>
    private async Task CallApplyRemoteChangesAsync(object store, FileTreeResponse response)
    {
        var field = typeof(SyncEngine).GetField("_remoteApplier",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(field);
        object applier = field!.GetValue(_engine)!;
        var method = applier.GetType().GetMethod("ApplyRemoteChangesAsync");
        Assert.NotNull(method);
        Task? task = (Task?)method!.Invoke(applier, [store, response, CancellationToken.None]);
        Assert.NotNull(task);
        await task;
    }

    /// <summary>反射设置 SyncPathSelector.SelectedPaths（排除集；T-099 拆分后 _selectedPaths 下沉）。</summary>
    private void SetSelectedPaths(SyncEngine engine, List<string> paths)
    {
        object selector = GetPathSelector(engine);
        var pathsProperty = selector.GetType().GetProperty("SelectedPaths");
        Assert.NotNull(pathsProperty);
        pathsProperty!.SetValue(selector, paths);
    }

    /// <summary>反射调用 SyncPathSelector.IsPathSelected（T-099 拆分后方法下沉）。</summary>
    private bool CallIsPathSelected(SyncEngine engine, string path)
    {
        object selector = GetPathSelector(engine);
        var method = selector.GetType().GetMethod("IsPathSelected", [typeof(string)]);
        Assert.NotNull(method);
        return (bool)method!.Invoke(selector, [path])!;
    }

    [Fact]
    public void IsPathSelected_取消勾选子树_该子树false其余路径true()
    {
        // 排除集：取消勾选 /photos → 排除该子树
        SetSelectedPaths(_engine, new List<string> { "/photos/" });

        Assert.False(CallIsPathSelected(_engine, "/photos/summer.jpg"));     // 子树内文件
        Assert.False(CallIsPathSelected(_engine, "/photos/sub/video.mp4"));  // 子树深层文件
        Assert.True(CallIsPathSelected(_engine, "/docs/report.pdf"));        // 其余目录文件
        Assert.True(CallIsPathSelected(_engine, "/readme.txt"));             // 根目录文件不受影响
    }

    [Fact]
    public void IsPathSelected_空集合_显式全不同步()
    {
        // 空集合 = 显式全不同步（取消全选），不再回退为 { "/" } 全选
        SetSelectedPaths(_engine, new List<string>());

        Assert.False(CallIsPathSelected(_engine, "/readme.txt"));
        Assert.False(CallIsPathSelected(_engine, "/photos/summer.jpg"));
        Assert.False(CallIsPathSelected(_engine, "/docs/report.pdf"));
    }

    [Fact]
    public void IsPathSelected_全选默认值_全选()
    {
        // ["/"] = 全选默认值（v1.0.0 既有默认，语义保持不变）
        SetSelectedPaths(_engine, new List<string> { "/" });

        Assert.True(CallIsPathSelected(_engine, "/readme.txt"));
        Assert.True(CallIsPathSelected(_engine, "/photos/summer.jpg"));
        Assert.True(CallIsPathSelected(_engine, "/docs/sub/x.txt"));
    }

    [Fact]
    public void IsPathSelected_旧版选择集含根_兼容全选()
    {
        // v1.0.0 旧版选择集恒含根 "/"（bug 使选择性同步实际等于全量）→ 兼容为全选，不误伤
        SetSelectedPaths(_engine, new List<string> { "/", "/photos/" });

        Assert.True(CallIsPathSelected(_engine, "/docs/report.pdf"));
        Assert.True(CallIsPathSelected(_engine, "/photos/summer.jpg"));
    }

    [Fact]
    public void IsPathSelected_嵌套排除子树_深层不同步且不误伤同级()
    {
        SetSelectedPaths(_engine, new List<string> { "/docs/private/" });

        Assert.False(CallIsPathSelected(_engine, "/docs/private/secret.txt"));
        Assert.True(CallIsPathSelected(_engine, "/docs/public.txt"));     // 同级文件不受影响
        Assert.True(CallIsPathSelected(_engine, "/docs/private2/x.txt")); // 前缀不误伤相似路径
    }

    // ============================================================
    // 排除集热更新（T-063）：UpdateSelectedPaths 引用替换，IsPathSelected 立即读新值
    // ============================================================

    [Fact]
    public void UpdateSelectedPaths_引用替换_IsPathSelected立即返回新值()
    {
        // 默认全选（构造时无 SelectedPaths → { "/" }）
        Assert.True(CallIsPathSelected(_engine, "/photos/summer.jpg"));

        // 保存设置后热更新排除集（无需重启客户端）：取消勾选 /photos
        _engine.UpdateSelectedPaths(new List<string> { "/photos/" });

        // 立即生效：方法返回后 IsPathSelected 已按新选择集判定
        Assert.False(CallIsPathSelected(_engine, "/photos/summer.jpg")); // 已排除子树 → false
        Assert.False(CallIsPathSelected(_engine, "/photos/sub/video.mp4"));
        Assert.True(CallIsPathSelected(_engine, "/docs/report.pdf"));    // 其余路径不受影响
        Assert.True(CallIsPathSelected(_engine, "/readme.txt"));
    }

    [Fact]
    public void UpdateSelectedPaths_热更新后再次更新_替换而非累积()
    {
        // 先排除 /photos，再切换为排除 /docs——验证引用替换语义（非启动快照，不累积）
        _engine.UpdateSelectedPaths(new List<string> { "/photos/" });
        Assert.False(CallIsPathSelected(_engine, "/photos/summer.jpg"));

        _engine.UpdateSelectedPaths(new List<string> { "/docs/" });

        // 第二次更新后：/photos 恢复选中、/docs 被排除（引用替换不累积旧选择）
        Assert.True(CallIsPathSelected(_engine, "/photos/summer.jpg"));
        Assert.False(CallIsPathSelected(_engine, "/docs/report.pdf"));
    }

    [Fact]
    public void UpdateSelectedPaths_空集合_显式全不同步()
    {
        _engine.UpdateSelectedPaths(new List<string>());

        Assert.False(CallIsPathSelected(_engine, "/readme.txt"));
        Assert.False(CallIsPathSelected(_engine, "/photos/summer.jpg"));
    }

    // ============================================================
    // 排除集语义闭环（T-054）：上传方向拦截 + 重新勾选恢复
    // ============================================================

    [Fact]
    public async Task EnqueueLocalChange_排除子树内新文件_不入队上传()
    {
        // 排除集：取消勾选 /photos → 子树内本地新建/修改文件不再上传
        SetSelectedPaths(_engine, new List<string> { "/photos/" });
        string filePath = Path.Combine(_syncRoot, "photos", "secret.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        await File.WriteAllTextAsync(filePath, "隐私内容");

        await _engine.EnqueueLocalChangeAsync("/photos/secret.txt", SyncOperation.Upload);

        await using var dbCheck = await _dbFactory.CreateDbContextAsync();
        Assert.Equal(0, await dbCheck.SyncQueue.CountAsync());
    }

    [Fact]
    public async Task EnqueueLocalChange_排除子树内删除_不入队不删服务端()
    {
        // 排除集：取消勾选 /photos → 删除本地残留副本不得传播（服务端副本保留，重新勾选后可再下载）
        SetSelectedPaths(_engine, new List<string> { "/photos/" });
        string filePath = Path.Combine(_syncRoot, "photos", "summer.jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        await File.WriteAllTextAsync(filePath, "jpeg-data");

        await _engine.EnqueueLocalChangeAsync("/photos/summer.jpg", SyncOperation.Delete);

        await using var dbCheck = await _dbFactory.CreateDbContextAsync();
        Assert.Equal(0, await dbCheck.SyncQueue.CountAsync());
    }

    [Fact]
    public async Task FullScan_排除子树内新建文件_不入队上传()
    {
        // 排除子树内新建文件（无快照）——修复前被当作新文件上传（上传方向泄漏）
        SetSelectedPaths(_engine, new List<string> { "/photos/" });
        string filePath = Path.Combine(_syncRoot, "photos", "new-secret.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        await File.WriteAllTextAsync(filePath, "新建的隐私文件");

        await _engine.FullScanAsync();

        await using var dbCheck = await _dbFactory.CreateDbContextAsync();
        int uploadCount = await dbCheck.SyncQueue.CountAsync(q =>
            q.FilePath == "/photos/new-secret.txt" && q.Operation == (int)SyncOperation.Upload);
        Assert.Equal(0, uploadCount);
    }

    [Fact]
    public async Task FullScan_排除子树内新建目录_不入队mkdir()
    {
        // 排除子树内新建目录（无快照）——不入队 mkdir（服务端不建立目录条目）
        SetSelectedPaths(_engine, new List<string> { "/photos/" });
        Directory.CreateDirectory(Path.Combine(_syncRoot, "photos", "albums"));

        await _engine.FullScanAsync();

        await using var dbCheck = await _dbFactory.CreateDbContextAsync();
        int mkdirCount = await dbCheck.SyncQueue.CountAsync(q =>
            q.FilePath == "/photos/albums" && q.Operation == (int)SyncOperation.Upload);
        Assert.Equal(0, mkdirCount);
    }

    [Fact]
    public async Task FullScan_重新勾选_本地副本恢复Synced()
    {
        // 场景：/photos 曾取消勾选（快照 CloudOnly + 本地残留副本），重新勾选（IsPathSelected 转 true）
        // → 本地存在 → 恢复 State（重置 CloudOnly → Synced），不再永久卡 CloudOnly
        string content = "残留的本地副本内容";
        string filePath = Path.Combine(_syncRoot, "photos", "summer.jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        await File.WriteAllTextAsync(filePath, content);
        string localHash = await FileHasher.ComputeSha256Async(filePath);

        await using (var setupDb = await _dbFactory.CreateDbContextAsync())
        {
            setupDb.RemoteSnapshots.Add(new RemoteSnapshot
            {
                Path = "/photos/summer.jpg", Type = (int)FileType.File,
                // T-085：Size 用文件实际字节数（WriteAllText 写 UTF-8，Chinese 内容字节数 ≠ content.Length 字符数），
                // 与 Hash 保持一致，才能真正验证『本地内容与快照一致 → 不重传』
                Size = new FileInfo(filePath).Length, Version = 3, State = (int)FileState.CloudOnly, Hash = localHash
            });
            await setupDb.SaveChangesAsync();
        }

        // 默认选择集 = 全选（重新勾选）
        await _engine.FullScanAsync();

        await using var dbCheck = await _dbFactory.CreateDbContextAsync();
        var snapshot = await dbCheck.RemoteSnapshots.FindAsync("/photos/summer.jpg");
        Assert.NotNull(snapshot);
        Assert.Equal((int)FileState.Synced, snapshot!.State); // 恢复 Synced
        Assert.True(snapshot.IsDownloaded);                    // 本地已落盘
        // 本地内容与快照一致 → 不重复上传（不振荡）
        Assert.Equal(0, await dbCheck.SyncQueue.CountAsync(q =>
            q.FilePath == "/photos/summer.jpg" && q.Operation == (int)SyncOperation.Upload));
    }

    [Fact]
    public async Task FullScan_重新勾选_本地缺失_入队下载()
    {
        // 场景：/photos 曾取消勾选（快照 CloudOnly，本地无副本），重新勾选 → 本地缺失 → 入队下载
        await using (var setupDb = await _dbFactory.CreateDbContextAsync())
        {
            setupDb.RemoteSnapshots.Add(new RemoteSnapshot
            {
                Path = "/photos/summer.jpg", Type = (int)FileType.File,
                Size = 9, Version = 3, State = (int)FileState.CloudOnly, Hash = "cloud-hash"
            });
            await setupDb.SaveChangesAsync();
        }

        await _engine.FullScanAsync();

        await using var dbCheck = await _dbFactory.CreateDbContextAsync();
        var item = await dbCheck.SyncQueue.FirstOrDefaultAsync(q =>
            q.FilePath == "/photos/summer.jpg" && q.Operation == (int)SyncOperation.Download);
        Assert.NotNull(item);
        Assert.Equal(3, item!.BaseVersion); // 版本相等也需下载（CloudOnly 从未落盘）
        // 快照保持 CloudOnly，下载完成后才置 Synced
        var snapshot = await dbCheck.RemoteSnapshots.FindAsync("/photos/summer.jpg");
        Assert.Equal((int)FileState.CloudOnly, snapshot!.State);
    }

    [Fact]
    public async Task ApplyRemoteChanges_重新勾选_版本相等_本地副本恢复Synced()
    {
        // 版本相等分支（T-054 修复点）：快照 CloudOnly、远端版本相等、本地残留副本存在
        // → 恢复 State（重置 CloudOnly → Synced），修复前该分支不恢复导致永久卡 CloudOnly
        string content = "残留副本";
        string filePath = Path.Combine(_syncRoot, "photos", "restore.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        await File.WriteAllTextAsync(filePath, content);
        string localHash = await FileHasher.ComputeSha256Async(filePath);

        await using (var setupDb = await _dbFactory.CreateDbContextAsync())
        {
            setupDb.RemoteSnapshots.Add(new RemoteSnapshot
            {
                Path = "/photos/restore.txt", Type = (int)FileType.File,
                Size = content.Length, Version = 3, State = (int)FileState.CloudOnly, Hash = localHash
            });
            await setupDb.SaveChangesAsync();
        }

        var response = new FileTreeResponse(
            new[]
            {
                new FileEntryDto("/photos/restore.txt", (int)FileType.File,
                    localHash, content.Length, 3, DateTime.UtcNow.ToString("O"), (int)FileState.Synced)
            },
            null, false, 3);

        await using var store = await _storeFactory.CreateStoreAsync();
        await CallApplyRemoteChangesAsync(store, response);
        await store.CommitAsync();

        var snapshot = await store.GetSnapshotAsync("/photos/restore.txt");
        Assert.NotNull(snapshot);
        Assert.Equal((int)FileState.Synced, snapshot!.State); // 重置 CloudOnly → Synced
        Assert.True(snapshot.IsDownloaded);
        // 版本相等 + 本地存在 → 不入队下载（内容一致无需重传）
        var restoreQueues = await store.GetQueuesByPathAsync("/photos/restore.txt", null);
        Assert.Empty(restoreQueues);
    }

    [Fact]
    public async Task ApplyRemoteChanges_重新勾选_版本相等_本地缺失_入队下载()
    {
        // 版本相等分支（T-054 修复点）：快照 CloudOnly、远端版本相等、本地无副本
        // → 入队下载（修复前版本相等分支不恢复 → 永久卡 CloudOnly，文件无法恢复）
        await using (var setupDb = await _dbFactory.CreateDbContextAsync())
        {
            setupDb.RemoteSnapshots.Add(new RemoteSnapshot
            {
                Path = "/photos/restore.txt", Type = (int)FileType.File,
                Size = 9, Version = 3, State = (int)FileState.CloudOnly, Hash = "cloud-hash"
            });
            await setupDb.SaveChangesAsync();
        }

        var response = new FileTreeResponse(
            new[]
            {
                new FileEntryDto("/photos/restore.txt", (int)FileType.File,
                    "cloud-hash", 9, 3, DateTime.UtcNow.ToString("O"), (int)FileState.Synced)
            },
            null, false, 3);

        await using var store = await _storeFactory.CreateStoreAsync();
        await CallApplyRemoteChangesAsync(store, response);
        await store.CommitAsync();

        // 已入队下载（版本相等也需下载，CloudOnly 从未落盘）
        var item = await store.GetQueueByPathAndOperationAsync("/photos/restore.txt", (int)SyncOperation.Download);
        Assert.NotNull(item);
        // 快照保持 CloudOnly，下载完成后 ProcessDownloadAsync 才置 Synced
        var snapshot = await store.GetSnapshotAsync("/photos/restore.txt");
        Assert.Equal((int)FileState.CloudOnly, snapshot!.State);
    }
}

/// <summary>测试用 ClientDbContext 工厂。</summary>
internal class TestClientDbFactory : IDbContextFactory<ClientDbContext>
{
    private readonly string _dbPath;
    public TestClientDbFactory(string dbPath) => _dbPath = dbPath;
    public ClientDbContext CreateDbContext() => new(_dbPath);
}
