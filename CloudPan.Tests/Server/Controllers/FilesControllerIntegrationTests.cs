using System.Net.Http.Json;
using System.Text.Json;
using CloudPan.Contract;
using Microsoft.AspNetCore.Mvc.Testing;
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
    private const string TestToken = "test-token-integration";
    private const string TestDeviceId = "test-device-001";
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
            builder.UseSetting("SyncRoot", _tempDir);
            // 通过环境变量注入测试 Token（app.Configuration 可访问）
            Environment.SetEnvironmentVariable("CloudPan__Token", TestToken);
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
        string testFilePath = Path.Combine(_tempDir, "test_upload.txt");
        await File.WriteAllTextAsync(testFilePath, "integration test content");

        using MultipartFormDataContent form = new MultipartFormDataContent();
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
        string testFilePath = Path.Combine(_tempDir, "empty_path.txt");
        await File.WriteAllTextAsync(testFilePath, "x");

        using MultipartFormDataContent form = new MultipartFormDataContent();
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
        string folderPath = $"/test-folder-{Guid.NewGuid():N}/";
        var response = await _client.PostAsJsonAsync("/api/files/mkdir",
            new { path = folderPath }, JsonOptions);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        // T-069/F-78：服务端 TrimEnd('/') 规范化，返回路径无尾斜杠
        Assert.Equal(folderPath.TrimEnd('/'), body.GetProperty("data").GetProperty("path").GetString());
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
        string testFilePath = Path.Combine(_tempDir, "for_tree.txt");
        await File.WriteAllTextAsync(testFilePath, "tree test");

        using MultipartFormDataContent form = new MultipartFormDataContent();
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
        // 上传关键词文件（本地文件名与远程路径不同，避免文件锁冲突）
        string localFile = Path.Combine(_tempDir, "_src_keyword_file.txt");
        await File.WriteAllTextAsync(localFile, "searchable");

        using MultipartFormDataContent form = new MultipartFormDataContent();
        await using var fileStream = File.OpenRead(localFile);
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
        string testFilePath = Path.Combine(_tempDir, "delete_me.txt");
        await File.WriteAllTextAsync(testFilePath, "to be deleted");

        using MultipartFormDataContent form = new MultipartFormDataContent();
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
    public async Task Delete_删除后_树返回Deleting墓碑()
    {
        // 上传一个文件
        string guid = Guid.NewGuid().ToString("N")[..8];
        string remotePath = $"/tombstone-{guid}.txt";
        string localFile = Path.Combine(_tempDir, $"_tomb_{guid}.txt");
        await File.WriteAllTextAsync(localFile, "to be tombstoned");

        using MultipartFormDataContent form = new MultipartFormDataContent();
        await using var fs = File.OpenRead(localFile);
        form.Add(new StreamContent(fs), "file", "tomb.txt");
        form.Add(new StringContent(remotePath), "path");
        form.Add(new StringContent("0"), "baseVersion");
        form.Add(new StringContent(DateTime.UtcNow.ToString("O")), "lastModified");
        var up = await _client.PostAsync("/api/files/upload", form);
        up.EnsureSuccessStatusCode();
        var upBody = await up.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        int version = upBody.GetProperty("data").GetProperty("version").GetInt32();

        // 删除
        var del = await _client.PostAsJsonAsync("/api/files/delete",
            new { path = remotePath, baseVersion = 0 }, JsonOptions);
        del.EnsureSuccessStatusCode();

        // 客户端以删除前版本为游标拉增量 → 收到 Deleting 墓碑（F-05 删除传播到客户端）
        var tree = await _client.GetAsync($"/api/files/tree?sinceVersion={version}");
        tree.EnsureSuccessStatusCode();
        var treeBody = await tree.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        var item = treeBody.GetProperty("data").EnumerateArray()
            .FirstOrDefault(d => d.GetProperty("path").GetString() == remotePath);
        Assert.NotEqual(default, item);
        Assert.Equal((int)CloudPan.Contract.FileState.Deleting, item.GetProperty("state").GetInt32());
    }

    [Fact]
    public async Task Move_重命名_成功()
    {
        string guid = Guid.NewGuid().ToString("N")[..8];
        string oldPath = $"/old-{guid}.txt";
        string newPath = $"/new-{guid}.txt";

        // 先上传
        string localFile = Path.Combine(_tempDir, $"old_{guid}.txt");
        await File.WriteAllTextAsync(localFile, "rename me");

        using MultipartFormDataContent form = new MultipartFormDataContent();
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

    // ============================================================
    // 下载 + 完整性校验
    // ============================================================

    [Fact]
    public async Task Download_上传后下载_内容一致且返回哈希头()
    {
        string guid = Guid.NewGuid().ToString("N")[..8];
        string remotePath = $"/download-test-{guid}.txt";
        string content = $"download integrity check {guid}";

        // 上传
        string localFile = Path.Combine(_tempDir, $"src_{guid}.txt");
        await File.WriteAllTextAsync(localFile, content);

        using MultipartFormDataContent form = new MultipartFormDataContent();
        await using var fileStream = File.OpenRead(localFile);
        form.Add(new StreamContent(fileStream), "file", $"src_{guid}.txt");
        form.Add(new StringContent(remotePath), "path");
        form.Add(new StringContent("0"), "baseVersion");
        form.Add(new StringContent(DateTime.UtcNow.ToString("O")), "lastModified");
        await _client.PostAsync("/api/files/upload", form);

        // 下载
        string downloadPath = Path.Combine(_tempDir, $"dl_{guid}.txt");
        var response = await _client.GetAsync(
            $"/api/files/download?path={Uri.EscapeDataString(remotePath)}");
        response.EnsureSuccessStatusCode();

        // 验证响应头包含哈希
        Assert.True(response.Headers.TryGetValues("X-File-Hash", out var hashValues));
        string hash = hashValues.First();
        Assert.Equal(64, hash.Length); // SHA-256 64 hex chars

        // 验证内容一致
        using var dlStream = await response.Content.ReadAsStreamAsync();
        using StreamReader reader = new StreamReader(dlStream);
        string downloadedContent = await reader.ReadToEndAsync();
        Assert.Equal(content, downloadedContent);
    }

    // ============================================================
    // 冲突检测
    // ============================================================

    [Fact]
    public async Task Upload_版本冲突_返回409和冲突副本()
    {
        string guid = Guid.NewGuid().ToString("N")[..8];
        string remotePath = $"/conflict-{guid}.txt";

        // 第一次上传 → 版本 1
        string localFile1 = Path.Combine(_tempDir, $"_src1_{guid}.txt");
        await File.WriteAllTextAsync(localFile1, "version 1");

        using (MultipartFormDataContent form = new MultipartFormDataContent())
        {
            await using var fs = File.OpenRead(localFile1);
            form.Add(new StreamContent(fs), "file", "file1.txt");
            form.Add(new StringContent(remotePath), "path");
            form.Add(new StringContent("0"), "baseVersion");
            form.Add(new StringContent(DateTime.UtcNow.ToString("O")), "lastModified");
            var r = await _client.PostAsync("/api/files/upload", form);
            r.EnsureSuccessStatusCode();
        }

        // 第二次上传（baseVersion=1 且版本匹配 → 正常覆盖 → 版本 2）
        string localFile2 = Path.Combine(_tempDir, $"_src2_{guid}.txt");
        await File.WriteAllTextAsync(localFile2, "version 2");

        using (MultipartFormDataContent form = new MultipartFormDataContent())
        {
            await using var fs = File.OpenRead(localFile2);
            form.Add(new StreamContent(fs), "file", "file2.txt");
            form.Add(new StringContent(remotePath), "path");
            form.Add(new StringContent("1"), "baseVersion"); // 当前版本是 1，匹配
            form.Add(new StringContent(DateTime.UtcNow.ToString("O")), "lastModified");
            var r = await _client.PostAsync("/api/files/upload", form);
            r.EnsureSuccessStatusCode(); // 正常覆盖
        }

        // 第三次上传（baseVersion=1，但服务端已是 v2 → 冲突！）
        string localFile3 = Path.Combine(_tempDir, $"_src3_{guid}.txt");
        await File.WriteAllTextAsync(localFile3, "version 3 - conflict!");

        using (MultipartFormDataContent form = new MultipartFormDataContent())
        {
            await using var fs = File.OpenRead(localFile3);
            form.Add(new StreamContent(fs), "file", "file3.txt");
            form.Add(new StringContent(remotePath), "path");
            form.Add(new StringContent("1"), "baseVersion"); // 过时！服务端已是 v2
            form.Add(new StringContent(DateTime.UtcNow.ToString("O")), "lastModified");
            var r = await _client.PostAsync("/api/files/upload", form);

            Assert.Equal(System.Net.HttpStatusCode.Conflict, r.StatusCode);

            var body = await r.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
            var error = body.GetProperty("error");
            Assert.Equal(HttpErrorCode.CONFLICT.Code, error.GetProperty("code").GetString());
            // Phase 1: conflictPath/currentVersion/baseVersion 已迁移到 detail 字段
            Assert.Contains("冲突", error.GetProperty("detail").GetString());
        }
    }

    // ============================================================
    // 版本历史（T-001：先存档后覆盖，回滚得到旧版本真实内容）
    // ============================================================

    /// <summary>上传一个文件到指定远程路径，返回服务端版本号。</summary>
    private async Task<int> UploadFileAsync(string remotePath, string localContent)
    {
        string localFile = Path.Combine(_tempDir, $"_src_{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(localFile, localContent);

        using MultipartFormDataContent form = new MultipartFormDataContent();
        await using var fs = File.OpenRead(localFile);
        form.Add(new StreamContent(fs), "file", "file.txt");
        form.Add(new StringContent(remotePath), "path");
        form.Add(new StringContent("0"), "baseVersion");
        form.Add(new StringContent(DateTime.UtcNow.ToString("O")), "lastModified");

        var response = await _client.PostAsync("/api/files/upload", form);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        return body.GetProperty("data").GetProperty("version").GetInt32();
    }

    [Fact]
    public async Task Upload_普通上传_版本历史回滚_内容为旧Version()
    {
        string guid = Guid.NewGuid().ToString("N")[..8];
        string remotePath = $"/version-archive-{guid}.txt";
        string version1Content = $"version 1 original {guid}";
        string version2Content = $"version 2 newer {guid}";

        // 第一次上传（版本 v1，旧内容）
        int v1 = await UploadFileAsync(remotePath, version1Content);

        // 第二次上传（版本 v2，新内容覆盖）
        int v2 = await UploadFileAsync(remotePath, version2Content);
        Assert.True(v2 > v1, "第二次上传应产生更大的版本号");

        // 版本历史应包含 v1 记录，且大小为旧内容长度（存档的是旧内容而非最新内容）
        var versionsResponse = await _client.GetAsync($"/api/versions?path={Uri.EscapeDataString(remotePath)}");
        versionsResponse.EnsureSuccessStatusCode();
        var versionsBody = await versionsResponse.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        var versions = versionsBody.GetProperty("data");
        Assert.True(versions.GetArrayLength() >= 1, "版本历史应至少包含一条记录");
        var v1Record = versions.EnumerateArray().First(v => v.GetProperty("version").GetInt32() == v1);
        Assert.Equal(version1Content.Length, v1Record.GetProperty("size").GetInt32());

        // 回滚到 v1 → 内容应为旧版本内容（修复前此断言失败：回滚得到的是 v2 最新内容）
        var restoreResponse = await _client.PostAsJsonAsync("/api/versions/restore",
            new { filePath = remotePath, version = v1 }, JsonOptions);
        restoreResponse.EnsureSuccessStatusCode();

        var downloadResponse = await _client.GetAsync($"/api/files/download?path={Uri.EscapeDataString(remotePath)}");
        downloadResponse.EnsureSuccessStatusCode();
        string downloaded = await downloadResponse.Content.ReadAsStringAsync();
        Assert.Equal(version1Content, downloaded);
    }

    // ============================================================
    // Token 认证
    // ============================================================

    [Fact]
    public async Task 无Token_返回401()
    {
        // 创建一个不带认证头的临时客户端
        using var noAuthClient = _factory.CreateClient();
        var response = await noAuthClient.GetAsync("/api/files/tree");
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
