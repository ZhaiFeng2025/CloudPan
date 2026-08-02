using CloudPan.Server.Services;
using CloudPan.Shared;
using Xunit;

namespace CloudPan.Tests.Server.Services;

/// <summary>
/// FileIndexService 单元测试——验证文件索引的 CRUD 操作。
/// </summary>
public class FileIndexServiceTests : Infrastructure.TestBase
{
    [Fact]
    public async Task UpsertFile_新建_返回新条目()
    {
        var dbFactory = CreateServerDbFactory();
        FileIndexService index = new FileIndexService(dbFactory);

        var entry = await index.UpsertFileAsync(
            "/test/file.txt", FileType.File, "abc123", 1024,
            DateTime.UtcNow.ToString("O"), 1);

        Assert.Equal("/test/file.txt", entry.Path);
        Assert.Equal(1, entry.Version);
        Assert.Equal("abc123", entry.CurrentHash);
    }

    [Fact]
    public async Task UpsertFile_更新_版本号和哈希变化()
    {
        var dbFactory = CreateServerDbFactory();
        FileIndexService index = new FileIndexService(dbFactory);

        await index.UpsertFileAsync("/a.txt", FileType.File, "hash1", 100,
            DateTime.UtcNow.ToString("O"), 1);

        var updated = await index.UpsertFileAsync("/a.txt", FileType.File, "hash2", 200,
            DateTime.UtcNow.ToString("O"), 5);

        Assert.Equal("hash2", updated.CurrentHash);
        Assert.Equal(200, updated.CurrentSize);
        Assert.Equal(5, updated.Version);
    }

    [Fact]
    public async Task GetByPath_存在_返回条目()
    {
        var dbFactory = CreateServerDbFactory();
        FileIndexService index = new FileIndexService(dbFactory);

        await index.UpsertFileAsync("/found.txt", FileType.File, "hash", 50,
            DateTime.UtcNow.ToString("O"), 1);

        var found = await index.GetByPathAsync("/found.txt");
        Assert.NotNull(found);
        Assert.Equal("/found.txt", found.Path);
    }

    [Fact]
    public async Task GetByPath_不存在_返回null()
    {
        var dbFactory = CreateServerDbFactory();
        FileIndexService index = new FileIndexService(dbFactory);

        var found = await index.GetByPathAsync("/ghost.txt");
        Assert.Null(found);
    }

    [Fact]
    public async Task Delete_文件_物理删除()
    {
        var dbFactory = CreateServerDbFactory();
        FileIndexService index = new FileIndexService(dbFactory);

        await index.UpsertFileAsync("/bye.txt", FileType.File, "hash", 1,
            DateTime.UtcNow.ToString("O"), 1);

        var deleted = await index.DeleteAsync("/bye.txt", isDirectory: false);
        Assert.Contains("/bye.txt", deleted);

        var found = await index.GetByPathAsync("/bye.txt");
        Assert.Null(found);
    }

    [Fact]
    public async Task Delete_文件夹_递归删除子文件()
    {
        var dbFactory = CreateServerDbFactory();
        FileIndexService index = new FileIndexService(dbFactory);

        await index.CreateDirectoryAsync("/parent/", 1);
        await index.UpsertFileAsync("/parent/child1.txt", FileType.File, "hash", 10,
            DateTime.UtcNow.ToString("O"), 2);
        await index.UpsertFileAsync("/parent/child2.txt", FileType.File, "hash", 20,
            DateTime.UtcNow.ToString("O"), 3);

        var deleted = await index.DeleteAsync("/parent/", isDirectory: true);

        Assert.Contains("/parent/", deleted);
        Assert.Contains("/parent/child1.txt", deleted);
        Assert.Contains("/parent/child2.txt", deleted);

        Assert.Null(await index.GetByPathAsync("/parent/"));
        Assert.Null(await index.GetByPathAsync("/parent/child1.txt"));
    }

    [Fact]
    public async Task Move_文件_路径更新()
    {
        var dbFactory = CreateServerDbFactory();
        FileIndexService index = new FileIndexService(dbFactory);

        await index.UpsertFileAsync("/old.txt", FileType.File, "hash", 1,
            DateTime.UtcNow.ToString("O"), 1);

        await index.MoveAsync("/old.txt", "/new.txt", 2, isDirectory: false);

        Assert.Null(await index.GetByPathAsync("/old.txt"));
        var moved = await index.GetByPathAsync("/new.txt");
        Assert.NotNull(moved);
        Assert.Equal(2, moved.Version);
    }

    [Fact]
    public async Task GetFileTree_分页_返回hasMore和nextCursor()
    {
        var dbFactory = CreateServerDbFactory();
        FileIndexService index = new FileIndexService(dbFactory);

        // 创建 5 个文件
        for (int i = 0; i < 5; i++)
        {
            await index.UpsertFileAsync($"/file_{i:D3}.txt", FileType.File, "hash", i,
                DateTime.UtcNow.ToString("O"), i + 1);
        }

        // 分页大小 2
        var page1 = await index.GetFileTreeAsync(sinceVersion: null, subPath: null, limit: 2);
        Assert.Equal(2, page1.Data.Length);
        Assert.True(page1.HasMore);
        Assert.NotNull(page1.NextCursor);

        // 第二页
        var page2 = await index.GetFileTreeAsync(sinceVersion: null, subPath: null, limit: 2, cursor: page1.NextCursor);
        Assert.Equal(2, page2.Data.Length);

        // 最后一页
        var page3 = await index.GetFileTreeAsync(sinceVersion: null, subPath: null, limit: 2, cursor: page2.NextCursor);
        Assert.Single(page3.Data);
        Assert.False(page3.HasMore);
    }

    [Fact]
    public async Task GetFileTree_增量_仅返回高版本文件()
    {
        var dbFactory = CreateServerDbFactory();
        FileIndexService index = new FileIndexService(dbFactory);

        await index.UpsertFileAsync("/v1.txt", FileType.File, "hash", 10,
            DateTime.UtcNow.ToString("O"), 1);
        await index.UpsertFileAsync("/v5.txt", FileType.File, "hash", 20,
            DateTime.UtcNow.ToString("O"), 5);
        await index.UpsertFileAsync("/v10.txt", FileType.File, "hash", 30,
            DateTime.UtcNow.ToString("O"), 10);

        var result = await index.GetFileTreeAsync(sinceVersion: 4);
        Assert.Equal(2, result.Data.Length); // v5 和 v10
        Assert.Contains(result.Data, d => d.Path == "/v5.txt");
        Assert.Contains(result.Data, d => d.Path == "/v10.txt");
    }

    [Fact]
    public async Task CreateDirectory_新建文件夹()
    {
        var dbFactory = CreateServerDbFactory();
        FileIndexService index = new FileIndexService(dbFactory);

        await index.CreateDirectoryAsync("/my-folder/", 1);

        var entry = await index.GetByPathAsync("/my-folder/");
        Assert.NotNull(entry);
        Assert.Equal((int)FileType.Directory, entry.Type);
    }

    [Fact]
    public async Task CreateDirectory_重复创建_抛出异常()
    {
        var dbFactory = CreateServerDbFactory();
        FileIndexService index = new FileIndexService(dbFactory);

        await index.CreateDirectoryAsync("/dup/", 1);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            index.CreateDirectoryAsync("/dup/", 2));
    }

    [Fact]
    public async Task Search_按关键词查找()
    {
        var dbFactory = CreateServerDbFactory();
        FileIndexService index = new FileIndexService(dbFactory);

        await index.UpsertFileAsync("/docs/report.docx", FileType.File, "hash", 100,
            DateTime.UtcNow.ToString("O"), 1);
        await index.UpsertFileAsync("/images/report.png", FileType.File, "hash", 200,
            DateTime.UtcNow.ToString("O"), 2);
        await index.UpsertFileAsync("/other.txt", FileType.File, "hash", 50,
            DateTime.UtcNow.ToString("O"), 3);

        var results = await index.SearchAsync("report");
        Assert.Equal(2, results.Count);
    }
}
