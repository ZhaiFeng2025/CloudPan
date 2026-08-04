using BenchmarkDotNet.Attributes;
using CloudPan.Client.Core.Models;
using CloudPan.Client.Core.Services;
using CloudPan.Contract;
using CloudPan.Infrastructure.Models;
using CloudPan.Infrastructure.Persistence.Client;
using CloudPan.Infrastructure.Storage;
using CloudPan.Tests.Client.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudPan.Tests.Benchmarks;

/// <summary>
/// 文件浏览查询性能基准（T-108）：数万文件同步根下 GetFileBrowserAsync 的查询延迟。
/// 验证浏览刷新不阻塞 UI：查询在后台线程执行（Task.Run），且查询耗时仅数十毫秒
/// （本地文件来自 FileSystemWatcher 增量缓存副本，快照来自内存缓存，无全树递归枚举 + 快照全表读取）。
/// 运行方式：dotnet run -c Release --project CloudPan.Tests -- --filter *BrowseQuery*
/// </summary>
[SimpleJob]
[MemoryDiagnoser]
public class BrowseQueryBenchmarks
{
    /// <summary>同步根内条目数（目录 + 文件，数万规模）。</summary>
    [Params(20_000, 50_000)]
    public int EntryCount { get; set; }

    private const int FilesPerDir = 100;
    private string _tempDir = "";
    private string _syncRoot = "";
    private SyncEngine? _engine;

    [GlobalSetup]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "CloudPanBench_Browse_" + Guid.NewGuid().ToString("N"));
        _syncRoot = Path.Combine(_tempDir, "sync");
        Directory.CreateDirectory(_syncRoot);

        string dbPath = Path.Combine(_tempDir, "client-bench.db");
        var dbFactory = new TestClientDbFactory(dbPath);
        using (var db = dbFactory.CreateDbContext())
        {
            db.Database.EnsureCreated();
            db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
        }

        // 快照与本地文件一致：dirCount 目录 + 每目录 FilesPerDir 文件（总数 ≈ EntryCount）
        int dirCount = Math.Max(1, EntryCount / (FilesPerDir + 1));
        int fileCount = EntryCount - dirCount;
        using (var db = dbFactory.CreateDbContext())
        {
            for (int d = 0; d < dirCount; d++)
            {
                string dirPath = $"/dir{d:D4}";
                db.RemoteSnapshots.Add(new RemoteSnapshot { Path = dirPath, Type = (int)FileType.Directory, State = (int)FileState.Synced, Version = 1 });
                int start = d * FilesPerDir;
                int end = Math.Min(start + FilesPerDir, fileCount);
                for (int f = start; f < end; f++)
                {
                    db.RemoteSnapshots.Add(new RemoteSnapshot
                    {
                        Path = $"/dir{d:D4}/file{d:D4}_{f:D5}.jpg",
                        Type = (int)FileType.File, State = (int)FileState.Synced,
                        Version = 1, Size = 1024
                    });
                }
            }
            db.SaveChanges();
        }

        // 本地文件实际落盘（模拟大照片库，供本地索引构建/增量缓存）
        for (int d = 0; d < dirCount; d++)
        {
            string localDir = Path.Combine(_syncRoot, $"dir{d:D4}");
            Directory.CreateDirectory(localDir);
            int start = d * FilesPerDir;
            int end = Math.Min(start + FilesPerDir, fileCount);
            for (int f = start; f < end; f++)
            {
                File.WriteAllText(Path.Combine(localDir, $"file{d:D4}_{f:D5}.jpg"), "x");
            }
        }

        var config = new SyncConfig { SyncRoot = _syncRoot, ServerUrl = "http://localhost:8443" };
        _engine = new SyncEngine(new MockApiClient(), config, new ClientStoreFactory(dbFactory),
            NullLoggerFactory.Instance.CreateLogger<SyncEngine>());
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // 清理失败不影响基准结果
        }
    }

    /// <summary>目录模式：根目录浏览（数万条目下仅返回直接子目录）。</summary>
    [Benchmark]
    public async Task<int> BrowseRootDirectory()
        => (await _engine!.GetFileBrowserAsync("/")).Count;

    /// <summary>搜索模式：全树名称匹配（数万条目下内存遍历 + 名称过滤）。</summary>
    [Benchmark]
    public async Task<int> BrowseSearch()
        => (await _engine!.GetFileBrowserAsync("/", "file")).Count;
}
