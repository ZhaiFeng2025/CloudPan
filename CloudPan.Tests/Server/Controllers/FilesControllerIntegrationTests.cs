using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace CloudPan.Tests.Server.Controllers;

/// <summary>
/// FilesController 集成测试——通过测试服务器验证完整 HTTP 请求-响应管线。
/// 使用 WebApplicationFactory 启动内存中的 ASP.NET Core 管道，无需真实端口。
/// </summary>
public class FilesControllerIntegrationTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;
    private readonly string _tempDir;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public FilesControllerIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"CloudPanIntegration_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        // 覆盖配置，使用临时目录作为同步根
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((ctx, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["SyncRoot"] = _tempDir
                });
            });
        });

        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    // ============================================================
    // 健康检查
    // ============================================================

    [Fact]
    public async Task Health_返回正常状态()
    {
        var response = await _client.GetAsync("/api/health");
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        Assert.Equal("ok", body.GetProperty("status").GetString());
    }

    // ============================================================
    // 文件上传/下载
    // ============================================================

    [Fact]
    public async Task Upload_正常文件_返回版本号()
    {
        // 准备测试文件
        var testFilePath = Path.Combine(_tempDir, "test_upload.txt");
        await File.WriteAllTextAsync(testFilePath, "integration test content");

        using var form = new MultipartFormDataContent();
        await using var fileStream = File.OpenRead(testFilePath);
        form.Add(new StreamContent(fileStream), "file", "test.txt");
        form.Add(new StringContent("/integration/test.txt"), "path");
        form.Add(new StringContent("0"), "baseVersion");
        form.Add(new StringContent(DateTime.UtcNow.ToString("O")), "lastModified");

        var response = await _client.PostAsync("/api/files/upload", form);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        var data = body.GetProperty("data");
        Assert.Equal("/integration/test.txt", data.GetProperty("path").GetString());
        Assert.True(data.GetProperty("version").GetInt32() > 0);
        Assert.NotEmpty(data.GetProperty("hash").GetString()!);
    }

    [Fact]
    public async Task Upload_空文件_返回400()
    {
        var response = await _client.PostAsync("/api/files/upload",
            new MultipartFormDataContent()); // 无文件

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Upload_空路径_返回400()
    {
        var testFilePath = Path.Combine(_tempDir, "empty_path.txt");
        await File.WriteAllTextAsync(testFilePath, "x");

        using var form = new MultipartFormDataContent();
        await using var fileStream = File.OpenRead(testFilePath);
        form.Add(new StreamContent(fileStream), "file", "test.txt");
        form.Add(new StringContent(""), "path"); // 空路径

        var response = await _client.PostAsync("/api/files/upload", form);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ============================================================
    // 文件夹操作
    // ============================================================

    [Fact]
    public async Task Mkdir_创建文件夹_返回路径()
    {
        var folderPath = $"/test-folder-{Guid.NewGuid():N}/";
        var response = await _client.PostAsJsonAsync("/api/files/mkdir",
            new { path = folderPath }, JsonOptions);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        Assert.Equal(folderPath, body.GetProperty("data").GetProperty("path").GetString());
    }

    [Fact]
    public async Task Mkdir_重复创建_返回409()
    {
        await _client.PostAsJsonAsync("/api/files/mkdir",
            new { path = "/dup-folder/" }, JsonOptions);

        var response = await _client.PostAsJsonAsync("/api/files/mkdir",
            new { path = "/dup-folder/" }, JsonOptions);

        Assert.Equal(System.Net.HttpStatusCode.Conflict, response.StatusCode);
    }

    // ============================================================
    // 文件树 / 搜索
    // ============================================================

    [Fact]
    public async Task GetTree_返回列表_包含hasMore字段()
    {
        var response = await _client.GetAsync("/api/files/tree");
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        // 不检查具体条数（共享数据库），只验证响应结构正确
        Assert.True(body.TryGetProperty("hasMore", out var hasMore));
        Assert.True(body.TryGetProperty("data", out _));
        Assert.True(body.TryGetProperty("maxVersion", out _));
    }

    [Fact]
    public async Task GetTree_上传后_包含文件()
    {
        // 先上传一个文件
        var testFilePath = Path.Combine(_tempDir, "for_tree.txt");
        await File.WriteAllTextAsync(testFilePath, "tree test");

        using var form = new MultipartFormDataContent();
        await using var fileStream = File.OpenRead(testFilePath);
        form.Add(new StreamContent(fileStream), "file", "for_tree.txt");
        form.Add(new StringContent("/tree-test.txt"), "path");
        form.Add(new StringContent("0"), "baseVersion");
        form.Add(new StringContent(DateTime.UtcNow.ToString("O")), "lastModified");
        await _client.PostAsync("/api/files/upload", form);

        // 查文件树
        var response = await _client.GetAsync("/api/files/tree");
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        Assert.True(body.GetProperty("data").GetArrayLength() >= 1);
    }

    [Fact]
    public async Task Search_匹配关键词_返回结果()
    {
        // 上传关键词文件
        var testFilePath = Path.Combine(_tempDir, "keyword_file.txt");
        await File.WriteAllTextAsync(testFilePath, "searchable");

        using var form = new MultipartFormDataContent();
        await using var fileStream = File.OpenRead(testFilePath);
        form.Add(new StreamContent(fileStream), "file", "keyword_file.txt");
        form.Add(new StringContent("/keyword_file.txt"), "path");
        form.Add(new StringContent("0"), "baseVersion");
        form.Add(new StringContent(DateTime.UtcNow.ToString("O")), "lastModified");
        await _client.PostAsync("/api/files/upload", form);

        var response = await _client.GetAsync("/api/files/search?q=keyword");
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        Assert.True(body.GetProperty("data").GetArrayLength() >= 1);
    }

    [Fact]
    public async Task Search_短关键词_返回400()
    {
        var response = await _client.GetAsync("/api/files/search?q=a");
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ============================================================
    // 删除 / 移动
    // ============================================================

    [Fact]
    public async Task Delete_上传后删除_成功()
    {
        // 上传
        var testFilePath = Path.Combine(_tempDir, "delete_me.txt");
        await File.WriteAllTextAsync(testFilePath, "to be deleted");

        using var form = new MultipartFormDataContent();
        await using var fileStream = File.OpenRead(testFilePath);
        form.Add(new StreamContent(fileStream), "file", "delete_me.txt");
        form.Add(new StringContent("/delete-me.txt"), "path");
        form.Add(new StringContent("0"), "baseVersion");
        form.Add(new StringContent(DateTime.UtcNow.ToString("O")), "lastModified");
        await _client.PostAsync("/api/files/upload", form);

        // 删除
        var response = await _client.PostAsJsonAsync("/api/files/delete",
            new { path = "/delete-me.txt", baseVersion = 0 }, JsonOptions);
        response.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Delete_不存在_返回404()
    {
        var response = await _client.PostAsJsonAsync("/api/files/delete",
            new { path = "/nonexistent.txt", baseVersion = 0 }, JsonOptions);

        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Move_重命名_成功()
    {
        var guid = Guid.NewGuid().ToString("N")[..8];
        var oldPath = $"/old-{guid}.txt";
        var newPath = $"/new-{guid}.txt";

        // 先上传
        var localFile = Path.Combine(_tempDir, $"old_{guid}.txt");
        await File.WriteAllTextAsync(localFile, "rename me");

        using var form = new MultipartFormDataContent();
        await using var fileStream = File.OpenRead(localFile);
        form.Add(new StreamContent(fileStream), "file", $"old_{guid}.txt");
        form.Add(new StringContent(oldPath), "path");
        form.Add(new StringContent("0"), "baseVersion");
        form.Add(new StringContent(DateTime.UtcNow.ToString("O")), "lastModified");
        await _client.PostAsync("/api/files/upload", form);

        // 移动
        var response = await _client.PostAsJsonAsync("/api/files/move",
            new { oldPath, newPath, baseVersion = 0 }, JsonOptions);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        Assert.Equal(newPath, body.GetProperty("data").GetProperty("newPath").GetString());
    }
}
