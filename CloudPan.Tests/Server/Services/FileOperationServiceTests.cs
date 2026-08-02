using System.Text;
using CloudPan.Server.Data;
using CloudPan.Server.Services;
using CloudPan.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CloudPan.Tests.Server.Services;

/// <summary>
/// FileOperationService 单元测试——删除/移动/建目录/下载/上传冲突（脱离 ASP.NET，直接注入领域服务）。
/// </summary>
public class FileOperationServiceTests : Infrastructure.TestBase
{
    private string SyncRoot => Path.Combine(TempDir, "sync");

    private Task<(FileOperationService svc, FileIndexService index)> CreateServiceAsync()
    {
        var dbFactory = CreateServerDbFactory();
        var storage = new FileStorageService(SyncRoot);
        var index = new FileIndexService(dbFactory);
        var version = new VersionService(dbFactory);
        var trash = new TrashService(storage, index, version);
        var syncLog = new SyncLogService(dbFactory, NullLogger<SyncLogService>.Instance);
        var svc = new FileOperationService(storage, index, version, trash, syncLog,
            NullLogger<FileOperationService>.Instance);
        return Task.FromResult((svc, index));
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
        var (svc, _) = await CreateServiceAsync();

        var result = await svc.MkdirAsync("/folder/");

        Assert.True(result.Success);
        Assert.Equal("/folder/", result.Path);
        Assert.True(Directory.Exists(Path.Combine(SyncRoot, "folder")));
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
        await index.CreateDirectoryAsync("/dir/", 1);

        var result = await svc.DownloadAsync("/dir/");

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
