using System.Net.Http.Json;
using System.Text.Json;
using CloudPan.Contract;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace CloudPan.Tests.Server.Controllers;

/// <summary>
/// 响应形状集成测试（T-040）。
/// 目标：服务端响应 JSON 与 ApiResponses.g.cs 生成 DTO 一一对应，客户端反序列化可解析。
/// 通过 WebApplicationFactory 启动内存中的 ASP.NET Core 管道，将响应反序列化为生成 DTO 并断言关键字段。
/// </summary>
public class ResponseShapeIntegrationTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;
    private readonly string _tempDir;
    private const string TestToken = "test-token-shape";
    private const string TestDeviceId = "test-device-shape";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public ResponseShapeIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"CloudPanShape_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        // 用 UseSetting 注入 Token（而非进程级环境变量），避免并行测试类互相覆盖 CloudPan__Token 导致认证竞态。
        // UseSetting 优先级高于环境变量配置源（与 WebSocketIntegrationTests 一致，T-040）。
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("SyncRoot", _tempDir);
            builder.UseSetting("CloudPan:Token", TestToken);
        });

        _client = _factory.CreateClient();
        _client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", TestToken);
        _client.DefaultRequestHeaders.Add("X-Device-Id", TestDeviceId);
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    /// <summary>上传一个文件到指定远程路径，返回反序列化后的 UploadResponse。</summary>
    private async Task<UploadResponse> UploadFileAsync(string remotePath, string content)
    {
        string localFile = Path.Combine(_tempDir, $"_src_{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(localFile, content);

        using MultipartFormDataContent form = new MultipartFormDataContent();
        await using var fs = File.OpenRead(localFile);
        form.Add(new StreamContent(fs), "file", "file.txt");
        form.Add(new StringContent(remotePath), "path");
        form.Add(new StringContent("0"), "baseVersion");
        form.Add(new StringContent(DateTime.UtcNow.ToString("O")), "lastModified");

        var response = await _client.PostAsync("/api/files/upload", form);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<UploadResponse>(JsonOptions)
            ?? throw new InvalidOperationException("上传响应无法反序列化为 UploadResponse");
    }

    // ============================================================
    // 响应 JSON 形状 ↔ 生成 DTO 反序列化
    // ============================================================

    [Fact]
    public async Task Health_反序列化为HealthResponse()
    {
        var response = await _client.GetAsync("/api/health");
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<HealthResponse>(JsonOptions);
        Assert.NotNull(body);
        Assert.Equal("ok", body!.Status);
        Assert.Equal("1.0.0", body.Version);
        Assert.True(body.MaxVersion >= 0);
        Assert.NotEmpty(body.SyncRoot);
    }

    [Fact]
    public async Task Upload_反序列化为UploadResponse()
    {
        var body = await UploadFileAsync($"/shape-upload-{Guid.NewGuid():N}.txt", "shape upload");

        Assert.Equal("/shape-upload-", body.Data.Path[.."/shape-upload-".Length]);
        Assert.True(body.Data.Version > 0);
        Assert.Equal(64, body.Data.Hash.Length);
        Assert.True(body.Data.Size > 0);
        Assert.False(body.Data.ConflictResolved);
    }

    [Fact]
    public async Task Mkdir_反序列化为MkdirResponse()
    {
        string folderPath = $"/shape-folder-{Guid.NewGuid():N}/";
        var response = await _client.PostAsJsonAsync("/api/files/mkdir", new { path = folderPath }, JsonOptions);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<MkdirResponse>(JsonOptions);
        Assert.NotNull(body);
        Assert.Equal(folderPath, body!.Data.Path);
    }

    [Fact]
    public async Task Delete_反序列化为DeleteResponse()
    {
        string remotePath = $"/shape-delete-{Guid.NewGuid():N}.txt";
        await UploadFileAsync(remotePath, "to delete");

        var response = await _client.PostAsJsonAsync("/api/files/delete",
            new { path = remotePath, baseVersion = 0 }, JsonOptions);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<DeleteResponse>(JsonOptions);
        Assert.NotNull(body);
        Assert.Equal(remotePath, body!.Data.Path);
        Assert.NotNull(body.Data.DeletedVersion);
    }

    [Fact]
    public async Task Move_反序列化为MoveResponse()
    {
        string guid = Guid.NewGuid().ToString("N")[..8];
        string oldPath = $"/shape-old-{guid}.txt";
        string newPath = $"/shape-new-{guid}.txt";
        await UploadFileAsync(oldPath, "to move");

        var response = await _client.PostAsJsonAsync("/api/files/move",
            new { oldPath, newPath, baseVersion = 0 }, JsonOptions);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<MoveResponse>(JsonOptions);
        Assert.NotNull(body);
        Assert.Equal(newPath, body!.Data.NewPath);
        Assert.Equal(oldPath, body.Data.OldPath);
    }

    [Fact]
    public async Task Search_反序列化为SearchResponse()
    {
        await UploadFileAsync("/shape-search.txt", "searchable content");

        var response = await _client.GetAsync("/api/files/search?q=shape-search");
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<SearchResponse>(JsonOptions);
        Assert.NotNull(body);
        Assert.NotEmpty(body!.Data);
        Assert.Equal("/shape-search.txt", body.Data[0].Path);
    }

    [Fact]
    public async Task Tree_反序列化为FileTreeResponse()
    {
        var response = await _client.GetAsync("/api/files/tree");
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<FileTreeResponse>(JsonOptions);
        Assert.NotNull(body);
        Assert.NotNull(body!.Data);
        Assert.True(body.MaxVersion >= 0); // 扁平字段（hasMore/maxVersion）可反序列化
    }

    [Fact]
    public async Task Trash_列表恢复清空_反序列化()
    {
        // 上传后删除 → 回收站
        string remotePath = $"/shape-trash-{Guid.NewGuid():N}.txt";
        await UploadFileAsync(remotePath, "to trash");
        await _client.PostAsJsonAsync("/api/files/delete", new { path = remotePath, baseVersion = 0 }, JsonOptions);

        // 列表 → TrashListResponse
        var listResponse = await _client.GetAsync("/api/trash");
        listResponse.EnsureSuccessStatusCode();
        var list = await listResponse.Content.ReadFromJsonAsync<TrashListResponse>(JsonOptions);
        Assert.NotNull(list);
        var target = list!.Data.FirstOrDefault(t => t.OriginalPath == remotePath);
        Assert.NotNull(target);
        Assert.NotEmpty(target.TrashFileName);

        // 恢复 → TrashRestoreResponse（返回原始路径）
        var restoreResponse = await _client.PostAsJsonAsync("/api/trash/restore",
            new { metaFileName = target.TrashFileName + ".json" }, JsonOptions);
        restoreResponse.EnsureSuccessStatusCode();
        var restore = await restoreResponse.Content.ReadFromJsonAsync<TrashRestoreResponse>(JsonOptions);
        Assert.NotNull(restore);
        Assert.Equal(remotePath, restore!.Data.Restored);

        // 再删除一次 → 清空 → TrashEmptyResponse
        await _client.PostAsJsonAsync("/api/files/delete", new { path = remotePath, baseVersion = 0 }, JsonOptions);
        var emptyResponse = await _client.DeleteAsync("/api/trash/empty");
        emptyResponse.EnsureSuccessStatusCode();
        var empty = await emptyResponse.Content.ReadFromJsonAsync<TrashEmptyResponse>(JsonOptions);
        Assert.NotNull(empty);
        Assert.Equal("trash emptied", empty!.Data);
    }

    [Fact]
    public async Task Versions_列表与回滚_反序列化()
    {
        string remotePath = $"/shape-versions-{Guid.NewGuid():N}.txt";
        await UploadFileAsync(remotePath, "v1");
        await UploadFileAsync(remotePath, "v2");

        // 列表 → VersionListResponse
        var listResponse = await _client.GetAsync($"/api/versions?path={Uri.EscapeDataString(remotePath)}");
        listResponse.EnsureSuccessStatusCode();
        var list = await listResponse.Content.ReadFromJsonAsync<VersionListResponse>(JsonOptions);
        Assert.NotNull(list);
        Assert.NotEmpty(list!.Data);
        Assert.True(list.Data.All(v => v.Version > 0));

        // 回滚到最早的版本 → VersionRestoreResponse
        int oldestVersion = list.Data.Min(v => v.Version);
        var restoreResponse = await _client.PostAsJsonAsync("/api/versions/restore",
            new { filePath = remotePath, version = oldestVersion }, JsonOptions);
        restoreResponse.EnsureSuccessStatusCode();
        var restore = await restoreResponse.Content.ReadFromJsonAsync<VersionRestoreResponse>(JsonOptions);
        Assert.NotNull(restore);
        Assert.Equal(remotePath, restore!.Data.Path);
        Assert.Equal(oldestVersion, restore.Data.RestoredFromVersion);
    }

    [Fact]
    public async Task Devices_反序列化为DevicesResponse()
    {
        var response = await _client.GetAsync("/api/devices");
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<DevicesResponse>(JsonOptions);
        Assert.NotNull(body);
        Assert.Contains(body!.Data, d => d.DeviceId == TestDeviceId); // 当前请求设备经中间件注册后应在列表中
    }

    [Fact]
    public async Task Shares_创建与撤销_反序列化()
    {
        string remotePath = $"/shape-share-{Guid.NewGuid():N}.txt";
        await UploadFileAsync(remotePath, "share me");

        // 创建 → ShareCreateResponse
        var createResponse = await _client.PostAsJsonAsync("/api/shares",
            new { filePath = remotePath }, JsonOptions);
        createResponse.EnsureSuccessStatusCode();
        var create = await createResponse.Content.ReadFromJsonAsync<ShareCreateResponse>(JsonOptions);
        Assert.NotNull(create);
        Assert.NotEmpty(create!.Data.ShareId);
        Assert.Contains($"/share/{create.Data.ShareId}", create.Data.Url);

        // 撤销 → ShareRevokeResponse
        var revokeResponse = await _client.DeleteAsync($"/api/shares/{create.Data.ShareId}");
        revokeResponse.EnsureSuccessStatusCode();
        var revoke = await revokeResponse.Content.ReadFromJsonAsync<ShareRevokeResponse>(JsonOptions);
        Assert.NotNull(revoke);
        Assert.Equal(create.Data.ShareId, revoke!.Data.Revoked);
    }

    [Fact]
    public async Task CertFingerprint_反序列化为CertFingerprintResponse()
    {
        var response = await _client.GetAsync("/api/cert-fingerprint");
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<CertFingerprintResponse>(JsonOptions);
        Assert.NotNull(body);
        Assert.IsType<string>(body!.Fingerprint);
    }

    // ============================================================
    // 管理面板响应 DTO（仅 localhost 认证，无法经 WebApplicationFactory 走 HTTP，
    // 改为验证 JSON 形状 ↔ DTO 字段名映射——AdminController 直接返回这些 DTO）
    // ============================================================

    [Fact]
    public void AdminStats_JSON形状与DTO一致()
    {
        var body = JsonSerializer.Deserialize<AdminStatsResponse>(
            """{"fileCount":1,"deviceCount":2,"onlineDeviceCount":1,"logCount":3}""", JsonOptions);
        Assert.NotNull(body);
        Assert.Equal(1, body!.FileCount);
        Assert.Equal(2, body.DeviceCount);
        Assert.Equal(1, body.OnlineDeviceCount);
        Assert.Equal(3, body.LogCount);
    }

    [Fact]
    public void Admin文件设备日志_JSON形状与DTO一致()
    {
        var file = JsonSerializer.Deserialize<AdminFileItem>(
            """{"path":"/a.txt","type":0,"currentHash":"h","currentSize":10,"version":1,"state":0,"lastModified":"2026-08-03T00:00:00Z"}""", JsonOptions);
        Assert.NotNull(file);
        Assert.Equal("/a.txt", file!.Path);
        Assert.Equal("h", file.CurrentHash);
        Assert.Equal(10, file.CurrentSize);

        var device = JsonSerializer.Deserialize<AdminDeviceItem>(
            """{"id":"server","name":"书房电脑","person":null,"lastSeen":"2026-08-03T00:00:00Z","online":1,"registeredAt":"2026-08-03T00:00:00Z"}""", JsonOptions);
        Assert.NotNull(device);
        Assert.Equal("server", device!.Id);
        Assert.Equal("书房电脑", device.Name);

        var log = JsonSerializer.Deserialize<AdminLogItem>(
            """{"id":1,"filePath":"/a.txt","operation":0,"deviceId":"server","result":0,"details":null,"createdAt":"2026-08-03T00:00:00Z"}""", JsonOptions);
        Assert.NotNull(log);
        Assert.Equal(1L, log!.Id);
        Assert.Equal("/a.txt", log.FilePath);
    }

    [Fact]
    public void Devices_JSON形状与DTO一致()
    {
        var body = JsonSerializer.Deserialize<DevicesResponse>(
            """{"data":[{"deviceId":"server","name":"书房电脑","person":null,"lastSeen":"2026-08-03T00:00:00Z","online":1,"registeredAt":"2026-08-03T00:00:00Z"}]}""", JsonOptions);
        Assert.NotNull(body);
        var item = Assert.Single(body!.Data);
        Assert.Equal("server", item.DeviceId);
        Assert.Equal("书房电脑", item.Name);
    }
}
