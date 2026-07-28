using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using CloudPan.Shared;

namespace CloudPan.Client.Services;

/// <summary>
/// 服务端 HTTP API 客户端。
/// Phase 0：HTTP 明文，无 Token 认证。
/// </summary>
public class ApiClient : IDisposable
{
    private readonly HttpClient _http;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public ApiClient(string baseUrl)
    {
        _http = new HttpClient { BaseAddress = new Uri(baseUrl.TrimEnd('/')) };
        _http.Timeout = TimeSpan.FromSeconds(60);
    }

    /// <summary>健康检查。</summary>
    public async Task<bool> HealthCheckAsync()
    {
        try
        {
            var response = await _http.GetAsync("/api/health");
            return response.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    /// <summary>获取文件树（增量）。</summary>
    public async Task<FileTreeApiResponse?> GetFileTreeAsync(int sinceVersion, int limit = 5000, string? subPath = null, string? cursor = null)
    {
        var url = $"/api/files/tree?sinceVersion={sinceVersion}&limit={limit}";
        if (!string.IsNullOrEmpty(subPath))
            url += $"&path={Uri.EscapeDataString(subPath)}";
        if (!string.IsNullOrEmpty(cursor))
            url += $"&cursor={Uri.EscapeDataString(cursor)}";
        var response = await _http.GetAsync(url);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<FileTreeApiResponse>(JsonOptions);
    }

    /// <summary>上传文件。</summary>
    public async Task<UploadApiResponse?> UploadAsync(
        string localPath, string remotePath, int baseVersion, string lastModified,
        IProgress<long>? progress = null)
    {
        using var form = new MultipartFormDataContent();
        var fileStream = File.OpenRead(localPath);
        var fileContent = new StreamContent(fileStream); // form 释放时自动释放 fileContent → fileStream

        form.Add(fileContent, "file", Path.GetFileName(remotePath));
        form.Add(new StringContent(remotePath), "path");
        form.Add(new StringContent(baseVersion.ToString()), "baseVersion");
        form.Add(new StringContent(lastModified), "lastModified");

        var response = await _http.PostAsync("/api/files/upload", form);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<UploadApiResponse>(JsonOptions);
    }

    /// <summary>下载文件。返回服务端文件最后修改时间。</summary>
    public async Task<string?> DownloadAsync(string remotePath, string localPath, IProgress<long>? progress = null)
    {
        var url = $"/api/files/download?path={Uri.EscapeDataString(remotePath)}";
        var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        var lastModified = response.Headers.TryGetValues("X-File-Modified", out var values)
            ? values.FirstOrDefault() : null;

        var dir = Path.GetDirectoryName(localPath);
        if (dir != null) Directory.CreateDirectory(dir);

        var tmpPath = localPath + ".tmp";
        await using (var stream = await response.Content.ReadAsStreamAsync())
        await using (var fileStream = File.Create(tmpPath))
        {
            await stream.CopyToAsync(fileStream);
        }

        // 原子替换
        if (File.Exists(localPath)) File.Delete(localPath);
        File.Move(tmpPath, localPath);

        return lastModified;
    }

    /// <summary>删除文件。</summary>
    public async Task DeleteAsync(string path, int baseVersion)
    {
        var response = await _http.PostAsJsonAsync("/api/files/delete",
            new { path, baseVersion }, JsonOptions);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>移动/重命名文件。</summary>
    public async Task MoveAsync(string oldPath, string newPath, int baseVersion)
    {
        var response = await _http.PostAsJsonAsync("/api/files/move",
            new { oldPath, newPath, baseVersion }, JsonOptions);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>创建文件夹。</summary>
    public async Task MkdirAsync(string path)
    {
        var response = await _http.PostAsJsonAsync("/api/files/mkdir",
            new { path }, JsonOptions);
        response.EnsureSuccessStatusCode();
    }

    public void Dispose() => _http.Dispose();
}

// ---- API 响应 DTO（与 shared-spec/apiMapping 对齐） ----

public class FileTreeApiResponse
{
    public List<FileEntryDto> Data { get; set; } = new();
    public string? NextCursor { get; set; }
    public bool HasMore { get; set; }
    public int MaxVersion { get; set; }
}

public class UploadApiResponse
{
    public UploadDataDto Data { get; set; } = new();
}

public class UploadDataDto
{
    public string Path { get; set; } = "";
    public int Version { get; set; }
    public string Hash { get; set; } = "";
    public int Size { get; set; }
    public bool ConflictResolved { get; set; }
}
