using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using CloudPan.Shared;

namespace CloudPan.Client.Services;

/// <summary>
/// 服务端 HTTP API 客户端。
/// Phase 0：HTTP 明文，无 Token 认证。
/// </summary>
public class ApiClient : IApiClient, IDisposable
{
    private readonly HttpClient _http;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// 创建 API 客户端。
    /// </summary>
    /// <param name="baseUrl">服务端地址。</param>
    /// <param name="token">家庭共享 Token。Phase 0 可传空字符串。</param>
    /// <param name="deviceId">设备 GUID。</param>
    public ApiClient(string baseUrl, string token = "", string deviceId = "")
    {
        _http = new HttpClient { BaseAddress = new Uri(baseUrl.TrimEnd('/')) };
        _http.Timeout = TimeSpan.FromSeconds(60);

        // 认证头（Phase 1a：Token 认证）
        if (!string.IsNullOrEmpty(token))
            _http.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        if (!string.IsNullOrEmpty(deviceId))
            _http.DefaultRequestHeaders.Add("X-Device-Id", deviceId);
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
    /// <exception cref="InvalidDataException">文件 SHA-256 与服务端不匹配（触发重传）。</exception>
    public async Task<string?> DownloadAsync(string remotePath, string localPath, IProgress<long>? progress = null)
    {
        var url = $"/api/files/download?path={Uri.EscapeDataString(remotePath)}";
        var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        var lastModified = response.Headers.TryGetValues("X-File-Modified", out var values)
            ? values.FirstOrDefault() : null;

        var expectedHash = response.Headers.TryGetValues("X-File-Hash", out var hashValues)
            ? hashValues.FirstOrDefault() : null;

        var dir = Path.GetDirectoryName(localPath);
        if (dir != null) Directory.CreateDirectory(dir);

        var tmpPath = localPath + ".tmp";
        await using (var stream = await response.Content.ReadAsStreamAsync())
        await using (var fileStream = File.Create(tmpPath))
        {
            await stream.CopyToAsync(fileStream);
        }

        // 下载后 SHA-256 校验（与 shared-spec.json §5 对齐）
        if (!string.IsNullOrEmpty(expectedHash))
        {
            var actualHash = await ComputeSha256Async(tmpPath);
            if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                SafeDelete(tmpPath);
                throw new InvalidDataException(
                    $"下载校验失败: {remotePath}。期望哈希: {expectedHash[..16]}..., 实际: {actualHash[..16]}...");
            }
        }

        // 原子替换（同卷 Move+overwrite 是原子的）
        File.Move(tmpPath, localPath, overwrite: true);

        return lastModified;
    }

    /// <summary>计算文件 SHA-256（64 字符十六进制）。</summary>
    private static async Task<string> ComputeSha256Async(string filePath)
    {
        using var sha = SHA256.Create();
        await using var stream = File.OpenRead(filePath);
        var hash = await sha.ComputeHashAsync(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>安全删除文件，不抛异常。</summary>
    private static void SafeDelete(string path)
    {
        try { File.Delete(path); } catch { }
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
