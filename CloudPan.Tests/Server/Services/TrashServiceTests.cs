using CloudPan.Server.Services;
using CloudPan.Shared;
using Xunit;

namespace CloudPan.Tests.Server.Services;

/// <summary>
/// TrashService 单元测试——回收站移入/列表/恢复/清空（脱离 ASP.NET，直接注入领域服务）。
/// 注意：同步根使用 TempDir/sync，回收站位于 TempDir/.cloudpan/.trash，随测试实例隔离。
/// </summary>
public class TrashServiceTests : Infrastructure.TestBase
{
    private string SyncRoot => Path.Combine(TempDir, "sync");
    private string TrashDir => Path.Combine(TempDir, ".cloudpan", ".trash");

    private async Task<TrashService> CreateServiceAsync(string fileName, string content)
    {
        var dbFactory = CreateServerDbFactory();
        var storage = new FileStorageService(SyncRoot);
        var index = new FileIndexService(dbFactory);
        var version = new VersionService(dbFactory);

        string abs = Path.Combine(SyncRoot, fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
        await File.WriteAllTextAsync(abs, content);
        await index.UpsertFileAsync($"/{fileName}", FileType.File, "hash", content.Length,
            DateTime.UtcNow.ToString("O"), 1);

        return new TrashService(storage, index, version);
    }

    [Fact]
    public async Task MoveToTrash_文件_进入回收站并生成元数据()
    {
        var svc = await CreateServiceAsync("a.txt", "hello trash");

        await svc.MoveToTrashAsync("/a.txt", isDirectory: false);

        // 原文件已移走
        Assert.False(File.Exists(Path.Combine(SyncRoot, "a.txt")));
        // 回收站有元数据与实体
        Assert.True(Directory.Exists(TrashDir));
        Assert.Single(Directory.GetFiles(TrashDir, "*.json"));
        Assert.Single(Directory.GetFiles(TrashDir), f => !f.EndsWith(".json"));
    }

    [Fact]
    public async Task List_移入后_返回条目()
    {
        var svc = await CreateServiceAsync("a.txt", "hello trash");
        await svc.MoveToTrashAsync("/a.txt", isDirectory: false);

        var items = await svc.ListAsync();

        var item = Assert.Single(items);
        Assert.Equal("/a.txt", item.OriginalPath);
        Assert.False(item.IsDirectory);
    }

    [Fact]
    public async Task Restore_文件_恢复到原位并重建索引()
    {
        var svc = await CreateServiceAsync("a.txt", "restore me");
        await svc.MoveToTrashAsync("/a.txt", isDirectory: false);
        string metaFile = Directory.GetFiles(TrashDir, "*.json").Single();
        string metaName = Path.GetFileName(metaFile);

        var result = await svc.RestoreAsync(metaName);

        Assert.True(result.Success);
        Assert.Equal("/a.txt", result.OriginalPath);
        Assert.True(File.Exists(Path.Combine(SyncRoot, "a.txt")));
        Assert.False(File.Exists(metaFile)); // 元数据已删除
    }

    [Fact]
    public async Task Restore_不存在的元数据_返回错误()
    {
        var svc = await CreateServiceAsync("a.txt", "x");

        var result = await svc.RestoreAsync("ghost.json");

        Assert.False(result.Success);
        Assert.Equal(HttpErrorCode.NOT_FOUND.Code, result.Error!.Code.Code);
    }

    [Fact]
    public async Task Empty_清空回收站()
    {
        var svc = await CreateServiceAsync("a.txt", "bye");
        await svc.MoveToTrashAsync("/a.txt", isDirectory: false);

        await svc.EmptyAsync();

        Assert.Empty(Directory.GetFiles(TrashDir, "*.json"));
    }
}
