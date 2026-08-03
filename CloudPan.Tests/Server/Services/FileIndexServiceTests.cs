using CloudPan.Contract;
using CloudPan.Server.Core;
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
    public async Task SoftDelete_文件_标记墓碑并保留条目()
    {
        var dbFactory = CreateServerDbFactory();
        FileIndexService index = new FileIndexService(dbFactory);

        await index.UpsertFileAsync("/bye.txt", FileType.File, "hash", 1,
            DateTime.UtcNow.ToString("O"), 1);

        var deleted = await index.SoftDeleteAsync("/bye.txt", isDirectory: false, newVersion: 5);
        Assert.Contains("/bye.txt", deleted);

        // 墓碑保留：条目仍在，标记 Deleting 并提升版本号（客户端增量同步据此删除本地副本）
        var found = await index.GetByPathAsync("/bye.txt");
        Assert.NotNull(found);
        Assert.Equal((int)FileState.Deleting, found.State);
        Assert.Equal(5, found.Version);
    }

    [Fact]
    public async Task SoftDelete_文件夹_递归标记子文件墓碑()
    {
        var dbFactory = CreateServerDbFactory();
        FileIndexService index = new FileIndexService(dbFactory);

        // T-049：对齐生产无尾斜杠约定（T-046）——目录条目以无尾斜杠路径存储。
        // 修复前仅前缀匹配（Path LIKE '/parent/%'）漏掉目录自身条目 → 目录 FileEntry 永不被墓碑化。
        await index.CreateDirectoryAsync("/parent", 1);
        await index.UpsertFileAsync("/parent/child1.txt", FileType.File, "hash", 10,
            DateTime.UtcNow.ToString("O"), 2);
        await index.UpsertFileAsync("/parent/child2.txt", FileType.File, "hash", 20,
            DateTime.UtcNow.ToString("O"), 3);

        var deleted = await index.SoftDeleteAsync("/parent", isDirectory: true, newVersion: 9);

        // 目录自身 + 后代均被墓碑化（F-49：目录自身条目不再被前缀匹配漏掉）
        Assert.Contains("/parent", deleted);
        Assert.Contains("/parent/child1.txt", deleted);
        Assert.Contains("/parent/child2.txt", deleted);

        // 墓碑保留：所有条目均在，标记 Deleting 且版本号提升
        Assert.Equal((int)FileState.Deleting, (await index.GetByPathAsync("/parent"))!.State);
        Assert.Equal(9, (await index.GetByPathAsync("/parent/child1.txt"))!.Version);
        Assert.Equal((int)FileState.Deleting, (await index.GetByPathAsync("/parent/child2.txt"))!.State);
    }

    [Fact]
    public async Task PurgeExpiredTombstones_超过保留窗口_物理清理()
    {
        var dbFactory = CreateServerDbFactory();
        FileIndexService index = new FileIndexService(dbFactory);

        // 插入一个"老"墓碑（LastModified 为 40 天前）
        string oldTs = DateTime.UtcNow.AddDays(-40).ToString("O");
        var db = dbFactory.CreateDbContext();
        db.FileEntries.Add(new CloudPan.Infrastructure.Models.FileEntry
        {
            Path = "/old-tomb.txt",
            Type = (int)FileType.File,
            CurrentHash = "hash",
            CurrentSize = 1,
            Version = 2,
            LastModified = oldTs,
            State = (int)FileState.Deleting,
            CreatedAt = oldTs
        });
        await db.SaveChangesAsync();
        db.Dispose();

        int purged = await index.PurgeExpiredTombstonesAsync(DateTime.UtcNow.AddDays(-30));
        Assert.Equal(1, purged);
        Assert.Null(await index.GetByPathAsync("/old-tomb.txt"));
    }

    [Fact]
    public async Task PurgeExpiredTombstones_未到期_保留()
    {
        var dbFactory = CreateServerDbFactory();
        FileIndexService index = new FileIndexService(dbFactory);

        await index.UpsertFileAsync("/fresh.txt", FileType.File, "hash", 1,
            DateTime.UtcNow.ToString("O"), 1);
        await index.SoftDeleteAsync("/fresh.txt", isDirectory: false, newVersion: 2);

        // cutoff 为 1 天前：新墓碑（创建于当前）未到期，不应清理
        int purged = await index.PurgeExpiredTombstonesAsync(DateTime.UtcNow.AddDays(-1));
        Assert.Equal(0, purged);
        Assert.NotNull(await index.GetByPathAsync("/fresh.txt")); // 未到期墓碑保留
    }

    [Fact]
    public async Task GetFileTree_增量_返回删除墓碑()
    {
        var dbFactory = CreateServerDbFactory();
        FileIndexService index = new FileIndexService(dbFactory);

        await index.UpsertFileAsync("/keep.txt", FileType.File, "hash", 10,
            DateTime.UtcNow.ToString("O"), 1);
        await index.UpsertFileAsync("/gone.txt", FileType.File, "hash", 20,
            DateTime.UtcNow.ToString("O"), 5);
        await index.SoftDeleteAsync("/gone.txt", isDirectory: false, newVersion: 10);

        var result = await index.GetFileTreeAsync(sinceVersion: 5);
        Assert.Single(result.Data); // 只有墓碑 /gone.txt（v10 > 5）
        Assert.Equal("/gone.txt", result.Data[0].Path);
        Assert.Equal((int)FileState.Deleting, result.Data[0].State);
    }

    [Fact]
    public async Task Search_排除删除墓碑()
    {
        var dbFactory = CreateServerDbFactory();
        FileIndexService index = new FileIndexService(dbFactory);

        await index.UpsertFileAsync("/report.txt", FileType.File, "hash", 10,
            DateTime.UtcNow.ToString("O"), 1);
        await index.UpsertFileAsync("/report-del.txt", FileType.File, "hash", 10,
            DateTime.UtcNow.ToString("O"), 2);
        await index.SoftDeleteAsync("/report-del.txt", isDirectory: false, newVersion: 3);

        var results = await index.SearchAsync("report");
        Assert.Single(results);
        Assert.Equal("/report.txt", results[0].Path);
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
    public async Task Move_目录_嵌套同名子目录路径正确()
    {
        var dbFactory = CreateServerDbFactory();
        FileIndexService index = new FileIndexService(dbFactory);

        // 嵌套同名目录场景（F-19）：/photos 下再建同名子目录 /photos，内含 img.jpg。
        // 旧 REPLACE(Path,'/photos/','/backup/photos/') 会替换路径内所有匹配段，
        // /photos/photos/img.jpg 中两处 /photos/ 均被替换 → /backup/photos/backup/photos/img.jpg 错乱。
        await index.CreateDirectoryAsync("/photos", 1);
        await index.CreateDirectoryAsync("/photos/photos", 2);
        await index.UpsertFileAsync("/photos/photos/img.jpg", FileType.File, "hash", 10,
            DateTime.UtcNow.ToString("O"), 3);

        await index.MoveAsync("/photos", "/backup/photos", 4, isDirectory: true);

        // 修复后按前缀长度裁剪拼接，结果应为 /backup/photos/photos/img.jpg
        Assert.Null(await index.GetByPathAsync("/photos/photos/img.jpg"));
        Assert.Null(await index.GetByPathAsync("/photos"));
        var movedFile = await index.GetByPathAsync("/backup/photos/photos/img.jpg");
        Assert.NotNull(movedFile);
        Assert.Equal(4, movedFile.Version);
        Assert.NotNull(await index.GetByPathAsync("/backup/photos"));
        Assert.NotNull(await index.GetByPathAsync("/backup/photos/photos"));
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

        // T-049：对齐生产无尾斜杠约定（T-046）——目录条目以无尾斜杠路径存储
        await index.CreateDirectoryAsync("/my-folder", 1);

        var entry = await index.GetByPathAsync("/my-folder");
        Assert.NotNull(entry);
        Assert.Equal((int)FileType.Directory, entry.Type);
    }

    [Fact]
    public async Task CreateDirectory_重复创建_抛出异常()
    {
        var dbFactory = CreateServerDbFactory();
        FileIndexService index = new FileIndexService(dbFactory);

        await index.CreateDirectoryAsync("/dup", 1);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            index.CreateDirectoryAsync("/dup", 2));
    }

    // T-049：目录软删（墓碑保留窗口内）后同名重建——墓碑复活为 Synced，不再因已存在路径抛 409
    [Fact]
    public async Task CreateDirectory_同名墓碑_复活为Synced目录()
    {
        var dbFactory = CreateServerDbFactory();
        FileIndexService index = new FileIndexService(dbFactory);

        await index.CreateDirectoryAsync("/revive", 1);
        await index.SoftDeleteAsync("/revive", isDirectory: true, newVersion: 2);

        // 同名重建：墓碑条目复活为有效目录（不抛 InvalidOperationException）
        await index.CreateDirectoryAsync("/revive", 3);

        var entry = await index.GetByPathAsync("/revive");
        Assert.NotNull(entry);
        Assert.Equal((int)FileType.Directory, entry.Type);
        Assert.Equal((int)FileState.Synced, entry.State);
        Assert.Equal(3, entry.Version);
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
