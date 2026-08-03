using System.Text;
using CloudPan.Contract;
using CloudPan.Infrastructure.Persistence;
using CloudPan.Infrastructure.Storage;
using CloudPan.Server.Core;
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
        var svc = new FileOperationService(storage, index, version, trash, syncLog,
            new ConflictBackupHelper(storage, index, version, syncLog),
            NullLogger<FileOperationService>.Instance);
        return Task.FromResult((svc, index));
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
