using System.Text.Json.Nodes;
using CloudPan.Contract;
using CloudPan.Infrastructure.Storage;
using CloudPan.Server.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CloudPan.Tests.Server.Services;

/// <summary>
/// TrashService 单元测试——回收站移入/列表/恢复/清空/保留期清理（脱离 ASP.NET，直接注入领域服务）。
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

        return new TrashService(storage, index, version, NullLogger<TrashService>.Instance);
    }

    /// <summary>把指定元数据文件的 DeletedAt 改写为给定时间（模拟历史保留期场景）。</summary>
    private static async Task RewriteDeletedAtAsync(string metaFile, DateTime deletedAt)
    {
        var node = JsonNode.Parse(await File.ReadAllTextAsync(metaFile))!.AsObject();
        node["DeletedAt"] = deletedAt.ToString("O");
        await File.WriteAllTextAsync(metaFile, node.ToJsonString());
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

    /// <summary>
    /// F-38/T-038：两个目录下同名文件同一秒删除（批量删除/重复清理真实场景）——
    /// 回收站文件名含 GUID 唯一化，第二个文件不再因 File.Move 目标已存在抛 IOException 被物理删除兜底丢失，
    /// 两个文件均进回收站且可分别恢复；meta 文件名 = TrashFileName + ".json" 不变量（客户端 SyncEngine.RestoreTrashAsync 依赖）不漂移。
    /// </summary>
    [Fact]
    public async Task MoveToTrash_同秒删除两个目录同名文件_均进回收站且可分别恢复()
    {
        var dbFactory = CreateServerDbFactory();
        var storage = new FileStorageService(SyncRoot);
        var index = new FileIndexService(dbFactory);
        var version = new VersionService(dbFactory);
        var svc = new TrashService(storage, index, version, NullLogger<TrashService>.Instance);

        // 两个目录下同名文件
        Directory.CreateDirectory(Path.Combine(SyncRoot, "dirA"));
        Directory.CreateDirectory(Path.Combine(SyncRoot, "dirB"));
        await File.WriteAllTextAsync(Path.Combine(SyncRoot, "dirA", "same.txt"), "AA");
        await File.WriteAllTextAsync(Path.Combine(SyncRoot, "dirB", "same.txt"), "BB");
        await index.UpsertFileAsync("/dirA/same.txt", FileType.File, "hashA", 2, DateTime.UtcNow.ToString("O"), 1);
        await index.UpsertFileAsync("/dirB/same.txt", FileType.File, "hashB", 2, DateTime.UtcNow.ToString("O"), 2);

        // 背靠背移入（同秒场景：秒级时间戳相同，靠 GUID 保证实体名唯一）
        await svc.MoveToTrashAsync("/dirA/same.txt", isDirectory: false);
        await svc.MoveToTrashAsync("/dirB/same.txt", isDirectory: false);

        // 两个文件均进回收站（实体、元数据各 2），且实体文件名互不相同（文件名唯一化）
        string[] entities = Directory.GetFiles(TrashDir).Where(f => !f.EndsWith(".json")).ToArray();
        string[] metas = Directory.GetFiles(TrashDir, "*.json");
        Assert.Equal(2, entities.Length);
        Assert.Equal(2, metas.Length);
        Assert.Equal(2, entities.Select(Path.GetFileName).Distinct().Count());

        // meta 文件名 = TrashFileName + ".json"（客户端 SyncEngine.RestoreTrashAsync 恢复依赖的命名不变量）
        foreach (string meta in metas)
        {
            var node = JsonNode.Parse(await File.ReadAllTextAsync(meta))!.AsObject();
            Assert.Equal(Path.GetFileName(meta), node["TrashFileName"]!.GetValue<string>() + ".json");
        }

        // 两个文件均可分别恢复
        foreach (string meta in metas)
        {
            var result = await svc.RestoreAsync(Path.GetFileName(meta));
            Assert.True(result.Success);
        }
        Assert.True(File.Exists(Path.Combine(SyncRoot, "dirA", "same.txt")));
        Assert.True(File.Exists(Path.Combine(SyncRoot, "dirB", "same.txt")));
    }

    /// <summary>
    /// F-24/T-024 路径穿越拒绝：MetaFileName 为用户输入，含目录分隔符（/、\）或绝对路径的穿越
    /// 一律返回 BAD_REQUEST，且不得读取/删除 trashDir 之外的文件。
    /// </summary>
    [Theory]
    [InlineData("../decoy.json")]
    [InlineData(@"..\..\secret.json")]
    [InlineData("../../server.db")]
    [InlineData("/etc/passwd")]
    public async Task Restore_路径穿越元数据文件名_被拒绝(string traversalName)
    {
        var svc = await CreateServiceAsync("a.txt", "x");
        await svc.MoveToTrashAsync("/a.txt", isDirectory: false);

        // 诱饵：trashDir 之外的 .cloudpan 下放置伪造元数据，若发生路径穿越会被读到/删除
        string decoyPath = Path.Combine(TempDir, ".cloudpan", "decoy.json");
        await File.WriteAllTextAsync(decoyPath, "{}");

        var result = await svc.RestoreAsync(traversalName);

        Assert.False(result.Success);
        Assert.Equal(HttpErrorCode.BAD_REQUEST.Code, result.Error!.Code.Code);
        // 穿越被拒绝：外部诱饵未被动过，回收站自身元数据未误删
        Assert.True(File.Exists(decoyPath));
        Assert.Single(Directory.GetFiles(TrashDir, "*.json"));
    }

    // ==================== T-026 保留期清理 PurgeExpiredAsync ====================

    [Fact]
    public async Task PurgeExpired_过期条目_实体与元数据一并清理()
    {
        var svc = await CreateServiceAsync("a.txt", "old content");
        await svc.MoveToTrashAsync("/a.txt", isDirectory: false);
        string metaFile = Directory.GetFiles(TrashDir, "*.json").Single();
        await RewriteDeletedAtAsync(metaFile, DateTime.UtcNow.AddDays(-40)); // 40 天前删除，超过 30 天保留期

        int purged = await svc.PurgeExpiredAsync(TimeSpan.FromDays(30));

        Assert.Equal(1, purged);
        // 元数据与实体文件均已清理
        Assert.Empty(Directory.GetFiles(TrashDir, "*.json"));
        Assert.Empty(Directory.GetFiles(TrashDir));
    }

    [Fact]
    public async Task PurgeExpired_未过期条目_保留()
    {
        var svc = await CreateServiceAsync("a.txt", "fresh content");
        await svc.MoveToTrashAsync("/a.txt", isDirectory: false); // DeletedAt = 现在

        int purged = await svc.PurgeExpiredAsync(TimeSpan.FromDays(30));

        Assert.Equal(0, purged);
        // 元数据与实体均保留
        Assert.Single(Directory.GetFiles(TrashDir, "*.json"));
        Assert.Single(Directory.GetFiles(TrashDir), f => !f.EndsWith(".json"));
    }

    [Fact]
    public async Task PurgeExpired_边界_恰好满保留期_清理()
    {
        var svc = await CreateServiceAsync("a.txt", "boundary");
        await svc.MoveToTrashAsync("/a.txt", isDirectory: false);
        string metaFile = Directory.GetFiles(TrashDir, "*.json").Single();
        // 恰好等于保留期（30 天前同一时刻），应视为过期清理
        await RewriteDeletedAtAsync(metaFile, DateTime.UtcNow.AddDays(-30));

        int purged = await svc.PurgeExpiredAsync(TimeSpan.FromDays(30));

        Assert.Equal(1, purged);
        Assert.Empty(Directory.GetFiles(TrashDir, "*.json"));
    }

    [Fact]
    public async Task PurgeExpired_元数据异常_损坏JSON跳过不误删()
    {
        var svc = await CreateServiceAsync("a.txt", "content");
        await svc.MoveToTrashAsync("/a.txt", isDirectory: false);
        string metaFile = Directory.GetFiles(TrashDir, "*.json").Single();
        await RewriteDeletedAtAsync(metaFile, DateTime.UtcNow.AddDays(-40));
        // 伪造损坏 JSON 元数据：解析失败应被跳过，不得中断整体清理、不得误删
        string corrupt = Path.Combine(TrashDir, "corrupt.json");
        await File.WriteAllTextAsync(corrupt, "{ 这不是合法 JSON ]");

        int purged = await svc.PurgeExpiredAsync(TimeSpan.FromDays(30));

        // 合法过期条目清理，损坏条目原样保留
        Assert.Equal(1, purged);
        Assert.Single(Directory.GetFiles(TrashDir, "*.json")); // 仅 corrupt.json 幸存
        Assert.Equal("corrupt.json", Path.GetFileName(Directory.GetFiles(TrashDir, "*.json").Single()));
        Assert.False(File.Exists(metaFile));
    }

    [Fact]
    public async Task PurgeExpired_元数据异常_缺字段或时间不可解析_跳过()
    {
        var svc = await CreateServiceAsync("a.txt", "content");
        await svc.MoveToTrashAsync("/a.txt", isDirectory: false);
        // 缺 TrashFileName 的异常条目
        var badMeta = Path.Combine(TrashDir, "bad.json");
        await File.WriteAllTextAsync(badMeta, """{"OriginalPath":"/a.txt","FileSize":1,"IsDirectory":false,"DeletedAt":"2026-01-01T00:00:00.0000000Z"}""");
        // 时间不可解析的异常条目
        var badTime = Path.Combine(TrashDir, "badtime.json");
        await File.WriteAllTextAsync(badTime, """{"OriginalPath":"/a.txt","TrashFileName":"x.bin","FileSize":1,"IsDirectory":false,"DeletedAt":"不是时间"}""");

        int purged = await svc.PurgeExpiredAsync(TimeSpan.FromDays(30));

        // 两条异常条目均被跳过（保留），不影响既有合法条目（未移入实体故 0 清理）
        Assert.Equal(0, purged);
        Assert.True(File.Exists(badMeta));
        Assert.True(File.Exists(badTime));
    }

    [Fact]
    public async Task PurgeExpired_回收站不存在_返回0()
    {
        var svc = await CreateServiceAsync("a.txt", "x"); // 未移入回收站，trashDir 不存在

        int purged = await svc.PurgeExpiredAsync(TimeSpan.FromDays(30));

        Assert.Equal(0, purged);
    }
}
