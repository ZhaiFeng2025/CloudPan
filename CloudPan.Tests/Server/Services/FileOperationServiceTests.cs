using System.Text;
using CloudPan.Contract;
using CloudPan.Infrastructure.Models;
using CloudPan.Infrastructure.Persistence;
using CloudPan.Infrastructure.Storage;
using CloudPan.Server.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CloudPan.Tests.Server.Services;

/// <summary>
/// FileOperationService 单元测试——删除/移动/建目录/下载/上传冲突（脱离 ASP.NET，直接注入领域服务）。
/// </summary>
public class FileOperationServiceTests : Infrastructure.TestBase
{
    private string SyncRoot => Path.Combine(TempDir, "sync");
    private string TrashDir => Path.Combine(TempDir, ".cloudpan", ".trash");

    private Task<(FileOperationService svc, FileIndexService index)> CreateServiceAsync(ITrashService? trashOverride = null)
    {
        var dbFactory = CreateServerDbFactory();
        var storage = new FileStorageService(SyncRoot);
        var index = new FileIndexService(dbFactory);
        var version = new VersionService(dbFactory);
        var trash = trashOverride ?? new TrashService(storage, index, version, NullLogger<TrashService>.Instance);
        var syncLog = new SyncLogService(dbFactory, NullLogger<SyncLogService>.Instance);
        var helper = new VersionCommitHelper(storage, NullLogger<VersionCommitHelper>.Instance);
        var versions = new VersionHistoryService(dbFactory, storage, index, version, syncLog, helper);
        var svc = new FileOperationService(storage, index, version, trash, syncLog,
            new ConflictBackupHelper(storage, index, version, syncLog),
            versions,
            NullLogger<FileOperationService>.Instance);
        return Task.FromResult((svc, index));
    }

    /// <summary>
    /// 完整领域服务装配（含 VersionHistoryService / UploadService，供版本历史跟随重命名测试使用）。
    /// FileIndexService 注入 storage——孤儿存档清理（PurgeOrphanVersionArchivesAsync）生效。
    /// </summary>
    private Task<(FileOperationService svc, FileIndexService index, VersionHistoryService versions,
        UploadService upload, IDbContextFactory<CloudPanDbContext> dbFactory)> CreateFullServiceAsync()
        => CreateFullServiceAsync(CreateServerDbFactory());

    private Task<(FileOperationService svc, FileIndexService index, VersionHistoryService versions,
        UploadService upload, IDbContextFactory<CloudPanDbContext> dbFactory)> CreateFullServiceAsync(
        IDbContextFactory<CloudPanDbContext> dbFactory)
    {
        var storage = new FileStorageService(SyncRoot);
        var index = new FileIndexService(dbFactory, storage);
        var version = new VersionService(dbFactory);
        var syncLog = new SyncLogService(dbFactory, NullLogger<SyncLogService>.Instance);
        var helper = new VersionCommitHelper(storage, NullLogger<VersionCommitHelper>.Instance);
        var trash = new TrashService(storage, index, version, NullLogger<TrashService>.Instance);
        var versions = new VersionHistoryService(dbFactory, storage, index, version, syncLog, helper);
        var svc = new FileOperationService(storage, index, version, trash, syncLog,
            new ConflictBackupHelper(storage, index, version, syncLog),
            versions,
            NullLogger<FileOperationService>.Instance);
        var upload = new UploadService(storage, svc, version, dbFactory, NullLogger<UploadService>.Instance, helper);
        return Task.FromResult((svc, index, versions, upload, dbFactory));
    }

    /// <summary>
    /// 生产对齐 dbFactory：连接串 Foreign Keys=True 启用外键约束（等价 PRAGMA foreign_keys=ON，
    /// 且 Microsoft.Data.Sqlite 池复用连接时该设置持久）。用于验证 FK 生效时重命名带版本历史文件
    /// 不再触发 FOREIGN KEY constraint failed（FileIndexService.MoveAsync 内 defer_foreign_keys
    /// 使 FileEntry 父键与 VersionRecords 子键同事务迁移、提交时引用一致）。
    /// </summary>
    private IDbContextFactory<CloudPanDbContext> CreateServerDbFactoryWithFk()
    {
        string dbPath = Path.Combine(TempDir, "test_fk.db");
        var options = new DbContextOptionsBuilder<CloudPanDbContext>()
            .UseSqlite($"Data Source={dbPath};Foreign Keys=True")
            .Options;

        using CloudPanDbContext db = new CloudPanDbContext(options);
        db.Database.EnsureCreated();
        db.Devices.Add(new Device
        {
            Id = "server",
            Name = "服务端",
            Person = null,
            LastSeen = DateTime.UtcNow.ToString("O"),
            Online = 1,
            RegisteredAt = DateTime.UtcNow.ToString("O")
        });
        db.AppConfigs.Add(new AppConfig { Key = "global_version", Value = "0" });
        db.SaveChanges();

        return new SimpleDbFactory(options);
    }

    private sealed class SimpleDbFactory : IDbContextFactory<CloudPanDbContext>
    {
        private readonly DbContextOptions<CloudPanDbContext> _options;
        public SimpleDbFactory(DbContextOptions<CloudPanDbContext> options) => _options = options;
        public CloudPanDbContext CreateDbContext() => new(_options);
    }

    /// <summary>同一文件连续三次上传，产生 2 条历史版本记录（第二次存档 v1、第三次存档 v2）与 2 个 .versions 存档文件。</summary>
    private static async Task SeedVersionHistoryAsync(UploadService upload, string path, string deviceId = "server")
    {
        await using var s1 = new MemoryStream(Encoding.UTF8.GetBytes("版本1 内容"));
        await upload.UploadAsync(path, s1, 12, 0, DateTime.UtcNow.ToString("O"), deviceId);
        await using var s2 = new MemoryStream(Encoding.UTF8.GetBytes("版本2 内容稍长一些"));
        await upload.UploadAsync(path, s2, 20, 0, DateTime.UtcNow.ToString("O"), deviceId);
        await using var s3 = new MemoryStream(Encoding.UTF8.GetBytes("版本3 内容再次更新"));
        await upload.UploadAsync(path, s3, 21, 0, DateTime.UtcNow.ToString("O"), deviceId);
    }

    private string VersionArchivePath(string storagePath) =>
        Path.Combine(SyncRoot, ".cloudpan", ".versions", storagePath);

    /// <summary>把 .versions 存档 mtime 回拨 1 小时，越过 T-088 孤儿回收的 10 分钟在途保护窗。</summary>
    private void BackdateAllArchives()
    {
        string dir = Path.Combine(SyncRoot, ".cloudpan", ".versions");
        if (!Directory.Exists(dir))
        {
            return;
        }
        DateTime old = DateTime.UtcNow.AddHours(-1);
        foreach (string file in Directory.EnumerateFiles(dir))
        {
            File.SetLastWriteTimeUtc(file, old);
        }
    }

    /// <summary>模拟移入回收站失败的 ITrashService（如 File.Move 抛 IOException——目标被占用/磁盘错误）。</summary>
    private sealed class ThrowingTrashService : ITrashService
    {
        // ITrashService.ListAsync 返回契约生成的 TrashItem（T-040：Server.Core 重复记录已删除，单一事实来源）
        public Task<List<CloudPan.Contract.TrashItem>> ListAsync() =>
            Task.FromResult(new List<CloudPan.Contract.TrashItem>());
        public Task<TrashRestoreResult> RestoreAsync(string metaFileName) =>
            Task.FromResult(new TrashRestoreResult(false, null,
                new DomainError(HttpErrorCode.NOT_FOUND, "无条目", "无条目")));
        public Task EmptyAsync() => Task.CompletedTask;
        public Task<int> PurgeExpiredAsync(TimeSpan retention) => Task.FromResult(0);
        public Task<string> MoveToTrashAsync(string relativePath, bool isDirectory) =>
            throw new IOException("目标已存在（模拟同秒同名碰撞移入失败）");
    }

    private static async Task SeedFileAsync(FileIndexService index, string absDir, string relPath, string content, int version = 1)
    {
        string abs = Path.Combine(absDir, relPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
        await File.WriteAllTextAsync(abs, content);
        await index.UpsertFileAsync(relPath, FileType.File, "hash", content.Length,
            DateTime.UtcNow.ToString("O"), version);
    }

    [Fact]
    public async Task Delete_文件_标记墓碑并进入回收站()
    {
        var (svc, index) = await CreateServiceAsync();
        await SeedFileAsync(index, SyncRoot, "/a.txt", "delete me");

        var result = await svc.DeleteAsync("/a.txt", 0, "dev-1");

        Assert.True(result.Success);
        Assert.NotNull(result.DeletedVersion);
        // 墓碑保留：条目仍在但标记 Deleting 并提升版本号（客户端增量同步据墓碑删除本地副本）
        var tomb = await index.GetByPathAsync("/a.txt");
        Assert.NotNull(tomb);
        Assert.Equal((int)FileState.Deleting, tomb.State);
        Assert.Equal(result.DeletedVersion, tomb.Version);
        Assert.False(File.Exists(Path.Combine(SyncRoot, "a.txt"))); // 已移入回收站
    }

    [Fact]
    public async Task Delete_文件_增量树返回墓碑_客户端据此删本地()
    {
        var (svc, index) = await CreateServiceAsync();
        await SeedFileAsync(index, SyncRoot, "/del.txt", "delete propagate");
        await svc.DeleteAsync("/del.txt", 0, "dev-1");

        // 客户端以删除前游标拉增量 → 收到 Deleting 墓碑（F-05 双向同步删除传播）
        var tree = await index.GetFileTreeAsync(sinceVersion: 0);
        var item = tree.Data.FirstOrDefault(d => d.Path == "/del.txt");
        Assert.NotNull(item);
        Assert.Equal((int)FileState.Deleting, item.State);
    }

    [Fact]
    public async Task Delete_不存在_返回错误()
    {
        var (svc, _) = await CreateServiceAsync();

        var result = await svc.DeleteAsync("/ghost.txt", 0, "dev-1");

        Assert.False(result.Success);
        Assert.Equal(HttpErrorCode.NOT_FOUND.Code, result.Error!.Code.Code);
    }

    [Fact]
    public async Task Delete_版本冲突_返回CONFLICT()
    {
        var (svc, index) = await CreateServiceAsync();
        await SeedFileAsync(index, SyncRoot, "/conflict.txt", "v2", version: 2);

        var result = await svc.DeleteAsync("/conflict.txt", baseVersion: 1, "dev-1");

        Assert.False(result.Success);
        Assert.Equal(HttpErrorCode.CONFLICT.Code, result.Error!.Code.Code);
        Assert.Contains("currentVersion=2", result.Error!.Detail);
    }

    /// <summary>
    /// F-38/T-038：移入回收站失败（File.Move 抛 IOException）时不再物理删除兜底——
    /// 保留原文件并返回错误；且因先移入回收站、成功后才软删索引，移入失败时索引保持 Synced，
    /// 不向客户端传播假删除，DB 与 FS 一致，调用方可提示用户重试。
    /// 方法名含 Trash 子串使 FQN 可被验收命令 dotnet test --filter Trash 命中。
    /// </summary>
    [Fact]
    public async Task Delete_MoveToTrash失败_保留原文件不物理删除()
    {
        var (svc, index) = await CreateServiceAsync(new ThrowingTrashService());
        await SeedFileAsync(index, SyncRoot, "/locked.txt", "keep me");

        var result = await svc.DeleteAsync("/locked.txt", 0, "dev-1");

        Assert.False(result.Success);
        Assert.Equal(HttpErrorCode.INTERNAL_ERROR.Code, result.Error!.Code.Code);
        // 原文件保留：不再物理删除兜底（回收站唯一兜底路径不静默丢数据）
        Assert.True(File.Exists(Path.Combine(SyncRoot, "locked.txt")));
        // 索引未被软删除（移入失败时索引未动，DB 与 FS 一致，可直接重试）
        var entry = await index.GetByPathAsync("/locked.txt");
        Assert.NotNull(entry);
        Assert.Equal((int)FileState.Synced, entry.State);
        // 回收站无残留
        Assert.False(Directory.Exists(TrashDir));
    }

    [Fact]
    public async Task Move_文件_重命名成功()
    {
        var (svc, index) = await CreateServiceAsync();
        await SeedFileAsync(index, SyncRoot, "/old.txt", "move me");

        var result = await svc.MoveAsync("/old.txt", "/new.txt", 0, "dev-1");

        Assert.True(result.Success);
        Assert.Equal("/new.txt", result.NewPath);
        Assert.Null(await index.GetByPathAsync("/old.txt"));
        Assert.NotNull(await index.GetByPathAsync("/new.txt"));
        Assert.True(File.Exists(Path.Combine(SyncRoot, "new.txt")));
    }

    /// <summary>
    /// T-077/F-119：重命名目标已存在（newPath 已有另一 FileEntry）时返回 CONFLICT 而非 500——
    /// 原实现 _index.MoveAsync 的 SQLite 主键 UPDATE 撞新路径 PK → 异常 500，且版本号已先被
    /// NextVersionAsync 无谓分配。目标检测须发生在版本分配之前。
    /// </summary>
    [Fact]
    public async Task Move_目标已存在_返回CONFLICT而非异常()
    {
        var (svc, index) = await CreateServiceAsync();
        await SeedFileAsync(index, SyncRoot, "/old.txt", "source");
        await SeedFileAsync(index, SyncRoot, "/target.txt", "existing target");

        var result = await svc.MoveAsync("/old.txt", "/target.txt", 0, "dev-1");

        Assert.False(result.Success);
        Assert.Equal(HttpErrorCode.CONFLICT.Code, result.Error!.Code.Code);
        Assert.Contains("目标已存在", result.Error!.UserMessage);
        // 目标检测在版本分配之前 → 未消耗版本号
        Assert.Null(result.Version);
        // 原文件与目标文件均未被改动（DB 与 FS 一致）
        Assert.NotNull(await index.GetByPathAsync("/old.txt"));
        Assert.NotNull(await index.GetByPathAsync("/target.txt"));
    }

    /// <summary>
    /// T-103/F-145：重命名文件后版本历史随文件移动——新路径 GetVersionsAsync 可查、旧路径不可达；
    /// 存档仍被迁移后的记录引用（回拨 mtime 后孤儿回收不误删仍在使用的存档）。
    /// </summary>
    [Fact]
    public async Task Move_重命名文件_版本历史跟随新路径旧路径不可达()
    {
        var (svc, index, versions, upload, _) = await CreateFullServiceAsync();
        await SeedVersionHistoryAsync(upload, "/photos/img.jpg");

        // 迁移前：旧路径有 2 条历史记录
        Assert.Equal(2, (await versions.GetVersionsAsync("/photos/img.jpg", 10)).Count);

        var result = await svc.MoveAsync("/photos/img.jpg", "/backup/img.jpg", 0, "dev-1");
        Assert.True(result.Success);

        // 新路径版本历史可查（随文件移动，2 条记录原样迁移）
        Assert.Equal(2, (await versions.GetVersionsAsync("/backup/img.jpg", 10)).Count);
        // 旧路径不可达
        Assert.Empty(await versions.GetVersionsAsync("/photos/img.jpg", 10));
        // 存档仍被迁移后的记录引用——孤儿回收不误删
        BackdateAllArchives();
        Assert.Equal(0, await index.PurgeOrphanVersionArchivesAsync());
        // 物理文件已移动
        Assert.False(File.Exists(Path.Combine(SyncRoot, "photos", "img.jpg")));
        Assert.True(File.Exists(Path.Combine(SyncRoot, "backup", "img.jpg")));
    }

    /// <summary>
    /// T-103/F-145：重命名目录后整棵子树的版本历史前缀迁移——新路径可查、旧路径不可达。
    /// </summary>
    [Fact]
    public async Task Move_重命名目录_子树版本历史跟随()
    {
        var (svc, index, versions, upload, _) = await CreateFullServiceAsync();
        Assert.True((await svc.MkdirAsync("/photos")).Success);
        await SeedVersionHistoryAsync(upload, "/photos/img.jpg");

        var result = await svc.MoveAsync("/photos", "/backup", 0, "dev-1");
        Assert.True(result.Success);

        // 新路径子树版本历史可查
        Assert.Equal(2, (await versions.GetVersionsAsync("/backup/img.jpg", 10)).Count);
        // 旧路径子树不可达
        Assert.Empty(await versions.GetVersionsAsync("/photos/img.jpg", 10));
        // 物理目录与文件已移动
        Assert.False(Directory.Exists(Path.Combine(SyncRoot, "photos")));
        Assert.True(File.Exists(Path.Combine(SyncRoot, "backup", "img.jpg")));
    }

    /// <summary>
    /// T-103/F-145 旧存档回收闭环：重命名迁移后旧路径无版本记录（不再引用 .versions 存档），
    /// 存档在迁移后仍被新路径记录引用（孤儿回收不误删）；当文件记录被清除（删除→墓碑物理清理）后，
    /// 旧存档（含重命名前产生）进入孤儿集合可被 T-088 PurgeOrphanVersionArchivesAsync 正常回收。
    /// </summary>
    [Fact]
    public async Task Move_版本历史迁移后_旧存档回收闭环()
    {
        var (svc, index, versions, upload, dbFactory) = await CreateFullServiceAsync();
        await SeedVersionHistoryAsync(upload, "/photos/img.jpg");

        // 收集旧路径存档文件名（迁移后仍被引用，记录清除后即孤儿）
        List<string> archiveNames;
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            archiveNames = await db.VersionRecords
                .Where(v => v.FilePath == "/photos/img.jpg")
                .Select(v => v.StoragePath)
                .ToListAsync();
        }
        Assert.Equal(2, archiveNames.Count);

        var result = await svc.MoveAsync("/photos/img.jpg", "/backup/img.jpg", 0, "dev-1");
        Assert.True(result.Success);

        // 迁移后：旧路径无任何版本记录（不再引用存档，T-088 回收不再被旧路径滞留）
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            Assert.Equal(0, await db.VersionRecords.CountAsync(v => v.FilePath == "/photos/img.jpg"));
            Assert.Equal(2, await db.VersionRecords.CountAsync(v => v.FilePath == "/backup/img.jpg"));
        }

        // 存档仍被新路径记录引用——孤儿回收不误删（回拨 mtime 越过 10 分钟在途保护窗）
        BackdateAllArchives();
        Assert.Equal(0, await index.PurgeOrphanVersionArchivesAsync());
        Assert.All(archiveNames, n => Assert.True(File.Exists(VersionArchivePath(n))));

        // 生命周期闭环：删除重命名后的文件 → 墓碑物理清理移除记录 + 存档 → 孤儿回收无残留
        var del = await svc.DeleteAsync("/backup/img.jpg", 0, "dev-1");
        Assert.True(del.Success);
        await index.PurgeExpiredTombstonesAsync(DateTime.UtcNow.AddSeconds(1));

        // 旧存档（含重命名前产生）已随记录清除，不再滞留在磁盘
        Assert.All(archiveNames, n => Assert.False(File.Exists(VersionArchivePath(n))));
        Assert.Equal(0, await index.PurgeOrphanVersionArchivesAsync());
    }

    /// <summary>
    /// T-103/F-145 + FK 语义：外键约束生效（生产对齐 Foreign Keys=True）时，重命名带版本历史的文件
    /// 不再因 UPDATE FileEntry 父键被 VersionRecord 子行引用而触发 FOREIGN KEY constraint failed；
    /// 版本历史随文件同事务迁移，新路径可查、旧路径不可达。
    /// </summary>
    [Fact]
    public async Task Move_外键启用时_重命名带版本历史文件_不触发FK失败且历史迁移()
    {
        var (svc, _, versions, upload, _) = await CreateFullServiceAsync(CreateServerDbFactoryWithFk());
        await SeedVersionHistoryAsync(upload, "/photos/img.jpg");

        var result = await svc.MoveAsync("/photos/img.jpg", "/backup/img.jpg", 0, "dev-1");

        Assert.True(result.Success);
        Assert.Equal(2, (await versions.GetVersionsAsync("/backup/img.jpg", 10)).Count);
        Assert.Empty(await versions.GetVersionsAsync("/photos/img.jpg", 10));
        Assert.True(File.Exists(Path.Combine(SyncRoot, "backup", "img.jpg")));
    }

    [Fact]
    public async Task Mkdir_创建目录_返回路径()
    {
        var (svc, index) = await CreateServiceAsync();

        var result = await svc.MkdirAsync("/folder/");

        Assert.True(result.Success);
        // T-069/F-78：带尾斜杠路径规范化后返回无尾斜杠路径，与 Windows 客户端 mkdir 一致
        Assert.Equal("/folder", result.Path);
        Assert.True(Directory.Exists(Path.Combine(SyncRoot, "folder")));
        // 入库无尾斜杠：尾斜杠路径无独立条目（不产生第二个 FileEntry 行）
        var entry = await index.GetByPathAsync("/folder");
        Assert.NotNull(entry);
        Assert.Equal((int)FileType.Directory, entry.Type);
        Assert.Null(await index.GetByPathAsync("/folder/"));
    }

    [Fact]
    public async Task Mkdir_重复创建_返回冲突()
    {
        var (svc, _) = await CreateServiceAsync();
        await svc.MkdirAsync("/dup/");

        var result = await svc.MkdirAsync("/dup/");

        Assert.False(result.Success);
        Assert.Equal(HttpErrorCode.CONFLICT.Code, result.Error!.Code.Code);
    }

    /// <summary>
    /// T-069/F-78：两端（Android 尾斜杠 / Windows 无尾斜杠）创建同一逻辑目录，
    /// 服务端 TrimEnd 归一后第二次命中已存在条目返回 CONFLICT，不产生两个 FileEntry 行。
    /// 方法名含 FileOperation 子串使 FQN 可被验收命令 dotnet test --filter FileOperation 命中。
    /// </summary>
    [Fact]
    public async Task Mkdir_两端创建同一目录_不产生两个FileEntry行()
    {
        var (svc, index) = await CreateServiceAsync();

        // Android：尾斜杠拼接
        var android = await svc.MkdirAsync("/shared/");
        // Windows：无尾斜杠
        var windows = await svc.MkdirAsync("/shared");

        Assert.True(android.Success);
        Assert.Equal("/shared", android.Path);
        Assert.False(windows.Success);
        Assert.Equal(HttpErrorCode.CONFLICT.Code, windows.Error!.Code.Code);
        // 仅一个条目，路径为无尾斜杠规范形态
        var entry = await index.GetByPathAsync("/shared");
        Assert.NotNull(entry);
        Assert.Equal((int)FileType.Directory, entry.Type);
        Assert.Null(await index.GetByPathAsync("/shared/"));
    }

    [Fact]
    public async Task Download_文件_返回流与大小()
    {
        var (svc, index) = await CreateServiceAsync();
        await SeedFileAsync(index, SyncRoot, "/dl.txt", "download content");

        var result = await svc.DownloadAsync("/dl.txt");

        Assert.True(result.Success);
        Assert.NotNull(result.Content);
        Assert.Equal("dl.txt", result.FileName);
        Assert.Equal("download content".Length, result.Size);
        using var reader = new StreamReader(result.Content!);
        Assert.Equal("download content", await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task Download_目录_返回错误()
    {
        var (svc, index) = await CreateServiceAsync();
        // T-069：目录条目统一无尾斜杠存储（CreateDirectoryAsync 亦 TrimEnd 归一）
        await index.CreateDirectoryAsync("/dir", 1);

        var result = await svc.DownloadAsync("/dir");

        Assert.False(result.Success);
        Assert.Equal(HttpErrorCode.BAD_REQUEST.Code, result.Error!.Code.Code);
    }

    [Fact]
    public async Task HandleUploadConflict_保存冲突副本()
    {
        var (svc, index) = await CreateServiceAsync();
        await SeedFileAsync(index, SyncRoot, "/base.txt", "base content", version: 5);
        await using var content = new MemoryStream(Encoding.UTF8.GetBytes("newer content"));

        var result = await svc.HandleUploadConflictAsync(
            "/base.txt", content, 13, null, baseVersion: 3, currentVersion: 5, deviceId: "dev-1");

        Assert.NotEmpty(result.ConflictPath);
        Assert.Contains("冲突", result.ConflictPath);
        Assert.Equal(5, result.CurrentVersion);
        Assert.Equal(3, result.BaseVersion);
        // 冲突副本文件已写入且索引为 Conflict
        string abs = Path.Combine(SyncRoot, result.ConflictPath.TrimStart('/'));
        Assert.True(File.Exists(abs));
        var entry = await index.GetByPathAsync(result.ConflictPath);
        Assert.NotNull(entry);
        Assert.Equal((int)FileState.Conflict, entry.State);
    }
}
