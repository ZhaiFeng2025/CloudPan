using CloudPan.Client.Core.Models;
using CloudPan.Infrastructure.Models;
using CloudPan.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CloudPan.Tests.Infrastructure;

/// <summary>
/// EF Migrations 替换 EnsureCreated（T-008）回归测试：
/// - 全新库经 Migrate() 建全表；
/// - 旧库（EnsureCreated 时代，无 __EFMigrationsHistory）经 Migrate() 升级不崩溃、数据保留，
///   缺失的表补建（迁移幂等 IF NOT EXISTS，替代原手写建表兼容层）；
/// - 旧客户端库 SyncQueue 缺 TargetPath 列 → 运行时 PRAGMA 判断 + ALTER 补列（数据保留）。
/// </summary>
public class MigrationsTests : IDisposable
{
    private readonly string _tempDir;

    public MigrationsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"CloudPanMigrations_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private string DbPath(string name) => Path.Combine(_tempDir, name);

    private static CloudPanDbContext CreateServerDb(string dbPath) =>
        new(new DbContextOptionsBuilder<CloudPanDbContext>().UseSqlite($"Data Source={dbPath}").Options);

    private static ClientDbContext CreateClientDb(string dbPath) =>
        new(new DbContextOptionsBuilder<ClientDbContext>().UseSqlite($"Data Source={dbPath}").Options);

    private static List<string> TableNames(DbContext db) =>
        db.Database.SqlQueryRaw<string>("SELECT name FROM sqlite_master WHERE type='table' ORDER BY name;").ToList();

    /// <summary>SyncQueue 是否已有 TargetPath 列（常量 SQL，避免 EF1002 插值告警）。</summary>
    private static bool SyncQueueHasTargetPath(DbContext db) =>
        db.Database.SqlQueryRaw<int>(
                "SELECT COUNT(*) FROM pragma_table_info('SyncQueue') WHERE name='TargetPath';")
            .ToList().First() > 0;

    [Fact]
    public void 服务端_全新库_Migrate_建全表()
    {
        string dbPath = DbPath("server-fresh.db");
        using CloudPanDbContext db = CreateServerDb(dbPath);
        db.Database.Migrate();

        var applied = db.Database.GetAppliedMigrations().ToList();
        Assert.Contains(applied, m => m.Contains("InitialCreate"));
        Assert.Contains(applied, m => m.Contains("AddChunkedUploadFinalized"));
        List<string> tables = TableNames(db);
        Assert.Contains("__EFMigrationsHistory", tables);
        foreach (string t in new[] { "FileEntry", "VersionRecord", "Device", "Share", "SyncLog", "ChunkedUpload", "AppConfig" })
            Assert.Contains(t, tables);
        // T-064：ChunkedUpload 已补 Finalized 列
        Assert.True(db.Database.SqlQueryRaw<int>(
                "SELECT COUNT(*) FROM pragma_table_info('ChunkedUpload') WHERE name='Finalized';")
            .ToList().First() > 0);
    }

    [Fact]
    public void 服务端_旧库_EnsureCreated时代_升级_数据保留且缺失表补建()
    {
        string dbPath = DbPath("server-legacy.db");
        // 阶段 1：模拟 EnsureCreated 时代的旧库（当前模型建表 + 种子数据，无迁移历史表）
        using (CloudPanDbContext db = CreateServerDb(dbPath))
        {
            db.Database.EnsureCreated();
            db.Devices.Add(new Device
            {
                Id = "server",
                Name = "服务端",
                LastSeen = DateTime.UtcNow.ToString("O"),
                Online = 1,
                RegisteredAt = DateTime.UtcNow.ToString("O")
            });
            db.AppConfigs.Add(new AppConfig { Key = "global_version", Value = "0" });
            db.SaveChanges();
            // 模拟旧库缺失后期新增的表（原 EnsureCreated 不补建，见 F-08/function 审查）
            db.Database.ExecuteSqlRaw("DROP TABLE Share;");
            // 模拟旧库无 Finalized 列：EnsureCreated 按当前模型建表含新列，移除以还原旧库形态（T-064 迁移补列）
            db.Database.ExecuteSqlRaw("ALTER TABLE ChunkedUpload DROP COLUMN Finalized;");
        }

        // 阶段 2：升级——Migrate 幂等：已有表跳过（数据保留）、缺失表补建、记录迁移历史
        using (CloudPanDbContext db = CreateServerDb(dbPath))
        {
            db.Database.Migrate();
        }

        // 阶段 3：验证
        using (CloudPanDbContext db = CreateServerDb(dbPath))
        {
            var applied = db.Database.GetAppliedMigrations().ToList();
            Assert.Contains(applied, m => m.Contains("InitialCreate"));
            Assert.Contains(applied, m => m.Contains("AddChunkedUploadFinalized"));
            // 种子数据保留
            Assert.Equal("服务端", db.Devices.Single(d => d.Id == "server").Name);
            Assert.Equal("0", db.AppConfigs.Single(c => c.Key == "global_version").Value);
            // 缺失表已补建
            Assert.Contains("Share", TableNames(db));
            // T-064：旧库升级后 ChunkedUpload 已补 Finalized 列
            Assert.True(db.Database.SqlQueryRaw<int>(
                    "SELECT COUNT(*) FROM pragma_table_info('ChunkedUpload') WHERE name='Finalized';")
                .ToList().First() > 0);
        }
    }

    [Fact]
    public void 客户端_全新库_Migrate_建全表()
    {
        string dbPath = DbPath("client-fresh.db");
        using ClientDbContext db = CreateClientDb(dbPath);
        db.Database.Migrate();

        // T-036 起客户端有多个迁移（InitialCreate + AddRemoteSnapshotLastModified + AddRemoteSnapshotIsDownloaded），不再用 Single() 断言
        var applied = db.Database.GetAppliedMigrations().ToList();
        Assert.Contains(applied, m => m.Contains("InitialCreate"));
        Assert.Contains(applied, m => m.Contains("AddRemoteSnapshotLastModified"));
        Assert.Contains(applied, m => m.Contains("AddRemoteSnapshotIsDownloaded"));
        List<string> tables = TableNames(db);
        Assert.Contains("__EFMigrationsHistory", tables);
        foreach (string t in new[] { "SyncQueue", "RemoteSnapshot", "SyncCursor" })
            Assert.Contains(t, tables);
        // T-036：RemoteSnapshots 已补 LastModified 列
        Assert.True(db.Database.SqlQueryRaw<int>(
                "SELECT COUNT(*) FROM pragma_table_info('RemoteSnapshot') WHERE name='LastModified';")
            .ToList().First() > 0);
        // T-037：RemoteSnapshots 已补 IsDownloaded 列（下载窗口保护标记）
        Assert.True(db.Database.SqlQueryRaw<int>(
                "SELECT COUNT(*) FROM pragma_table_info('RemoteSnapshot') WHERE name='IsDownloaded';")
            .ToList().First() > 0);
    }

    [Fact]
    public void 客户端_旧库_SyncQueue缺TargetPath_升级补列_数据保留()
    {
        string dbPath = DbPath("client-legacy.db");
        // 阶段 1：模拟 EnsureCreated 时代的旧客户端库——SyncQueue 无 TargetPath 列（见 F-08/function 审查）
        using (ClientDbContext db = CreateClientDb(dbPath))
        {
            db.Database.ExecuteSqlRaw("""
                CREATE TABLE RemoteSnapshots (
                    "Path" TEXT NOT NULL PRIMARY KEY,
                    "Type" INTEGER NOT NULL,
                    "Hash" TEXT NULL,
                    "Size" INTEGER NOT NULL,
                    "Version" INTEGER NOT NULL,
                    "State" INTEGER NOT NULL
                );
                CREATE TABLE SyncCursor (
                    "Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    "LastMaxVersion" INTEGER NOT NULL,
                    "LastSyncAt" TEXT NULL
                );
                CREATE TABLE SyncQueue (
                    "Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    "FilePath" TEXT NOT NULL,
                    "Operation" INTEGER NOT NULL,
                    "Priority" INTEGER NOT NULL,
                    "BaseVersion" INTEGER NULL,
                    "FileSize" INTEGER NULL,
                    "RetryCount" INTEGER NOT NULL,
                    "LastError" TEXT NULL,
                    "CreatedAt" TEXT NOT NULL
                );
                INSERT INTO SyncQueue (FilePath, Operation, Priority, RetryCount, CreatedAt)
                    VALUES ('/old.txt', 0, 1, 0, '2026-01-01T00:00:00.0000000Z');
                """);
        }

        // 阶段 2：升级——Migrate 幂等跳过已有表（数据保留），记录迁移历史
        using (ClientDbContext db = CreateClientDb(dbPath))
        {
            db.Database.Migrate();
        }

        // 阶段 3：列级兼容——镜像运行时 EnsureSyncQueueTargetPathColumn（PRAGMA 判断 + ALTER 补列）
        using (ClientDbContext db = CreateClientDb(dbPath))
        {
            Assert.False(SyncQueueHasTargetPath(db)); // 迁移不自动补列（SQLite ADD COLUMN 无 IF NOT EXISTS）
            db.Database.ExecuteSqlRaw("ALTER TABLE SyncQueue ADD COLUMN TargetPath TEXT NULL;");
        }

        // 阶段 4：验证——数据保留、TargetPath 列可读
        using (ClientDbContext db = CreateClientDb(dbPath))
        {
            Assert.True(SyncQueueHasTargetPath(db));
            // T-036 起客户端有多个迁移，不再用 Single() 断言
            var applied = db.Database.GetAppliedMigrations().ToList();
            Assert.Contains(applied, m => m.Contains("InitialCreate"));
            Assert.Contains(applied, m => m.Contains("AddRemoteSnapshotLastModified"));
            Assert.Contains(applied, m => m.Contains("AddRemoteSnapshotIsDownloaded"));
            // T-037：旧库升级后 RemoteSnapshots 补 IsDownloaded 列
            Assert.True(db.Database.SqlQueryRaw<int>(
                    "SELECT COUNT(*) FROM pragma_table_info('RemoteSnapshot') WHERE name='IsDownloaded';")
                .ToList().First() > 0);
            SyncQueue row = db.SyncQueue.Single();
            Assert.Equal("/old.txt", row.FilePath);
            Assert.Equal(0, row.RetryCount);
            Assert.Null(row.TargetPath);
        }
    }
}
