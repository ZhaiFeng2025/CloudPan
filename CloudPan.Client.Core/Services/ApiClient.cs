using System.Net.Http.Json;
using System.Text.Json;
using CloudPan.Contract;
using Microsoft.Extensions.Logging;

namespace CloudPan.Client.Core.Services;

/// <summary>
/// 服务端 HTTP API 客户端。
/// 支持 Bearer Token 认证、X-Device-Id 设备标识、直传与分块上传。
/// Phase 0 对自签证书静默接受（ServerCertificateCustomValidationCallback 始终返回 true）。
/// </summary>
public partial class ApiClient : IApiClient, IDisposable
{
    private readonly HttpClient _http;
    // T-063：限速改为运行时可变（非构造固化）。long 不能声明为 volatile（C# 限制），
    // 读写经 Interlocked 保证 32-bit 运行时原子性（CLAUDE.md 7.4），运行中改限速立即生效。
    private long _uploadLimitBps;
    private long _downloadLimitBps;
    private readonly ILogger? _logger;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// 创建 API 客户端。
    /// </summary>
    public ApiClient(string baseUrl, string token = "", string deviceId = "",
        long uploadLimitBps = 0, long downloadLimitBps = 0,
        ILogger<ApiClient>? logger = null)
    {
        HttpClientHandler handler = new HttpClientHandler
        {
            // Phase 0：自签证书，静默接受（TOFU 简化——始终信任）
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
            // 局域网直连：不走系统代理（代理会拦截 localhost/局域网请求导致连接失败）
            UseProxy = false
        };

        _http = new HttpClient(handler) { BaseAddress = new Uri(baseUrl.TrimEnd('/')) };
        _http.Timeout = TimeSpan.FromSeconds(30); // 30 秒超时后抛出 TaskCanceledException
        _uploadLimitBps = uploadLimitBps;
        _downloadLimitBps = downloadLimitBps;
        _logger = logger;

        // 认证头
        if (!string.IsNullOrEmpty(token))
        {
            _http.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        }

        if (!string.IsNullOrEmpty(deviceId))
        {
            _http.DefaultRequestHeaders.Add("X-Device-Id", deviceId);
        }
    }

    /// <summary>运行时更新上传限速（T-063，无需重启客户端）。0 = 不限速。后续传输立即按新限速节流。</summary>
    public void SetUploadLimit(long bytesPerSecond)
    {
        Interlocked.Exchange(ref _uploadLimitBps, bytesPerSecond);
    }

    /// <summary>运行时更新下载限速（T-063，无需重启客户端）。0 = 不限速。后续传输立即按新限速节流。</summary>
    public void SetDownloadLimit(long bytesPerSecond)
    {
        Interlocked.Exchange(ref _downloadLimitBps, bytesPerSecond);
    }

    /// <summary>健康检查。</summary>
    public async Task<bool> HealthCheckAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await _http.GetAsync(SpecRoutes.Health, ct);
            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "健康检查失败");
            return false;
        }
    }

    /// <summary>
    /// 健康检查（设置页测试连接用，T-053）：失败抛底层异常供白话归因，不再吞异常返回 false。
    /// 与 HealthCheckAsync 区分：后者供后台轮询（只关心是否连上），本方法供交互式测试（需要解释失败原因）。
    /// </summary>
    public async Task EnsureHealthAsync(CancellationToken ct = default)
    {
        var response = await _http.GetAsync(SpecRoutes.Health, ct);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>获取文件树（增量）。</summary>
    public async Task<FileTreeResponse?> GetFileTreeAsync(int sinceVersion, int limit = 5000, string? subPath = null, string? cursor = null, CancellationToken ct = default)
    {
        string url = $"{SpecRoutes.FilesTree}?sinceVersion={sinceVersion}&limit={limit}";
        if (!string.IsNullOrEmpty(subPath))
        {
            url += $"&path={Uri.EscapeDataString(subPath)}";
        }

        if (!string.IsNullOrEmpty(cursor))
        {
            url += $"&cursor={Uri.EscapeDataString(cursor)}";
        }

        var response = await _http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<FileTreeResponse>(JsonOptions, ct);
    }

    /// <summary>上传文件。</summary>
    public async Task<UploadResponse?> UploadAsync(
        string localPath, string remotePath, int baseVersion, string lastModified,
        IProgress<long>? progress = null, CancellationToken ct = default)
    {
        using MultipartFormDataContent form = new MultipartFormDataContent();
        Stream fileStream = File.OpenRead(localPath);
        long uploadLimit = Interlocked.Read(ref _uploadLimitBps); // T-063：运行时可变，每次传输读当前值
        if (uploadLimit > 0)
        {
            fileStream = new ThrottledStream(fileStream, uploadLimit);
        }

        StreamContent fileContent = new StreamContent(fileStream); // form 释放时自动释放 fileContent → fileStream

        form.Add(fileContent, "file", Path.GetFileName(remotePath));
        form.Add(new StringContent(remotePath), "path");
        form.Add(new StringContent(baseVersion.ToString()), "baseVersion");
        form.Add(new StringContent(lastModified), "lastModified");

        var response = await _http.PostAsync(SpecRoutes.FilesUpload, form, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<UploadResponse>(JsonOptions, ct);
    }

    /// <summary>下载文件。返回服务端文件最后修改时间和期望哈希。</summary>
    /// <exception cref="InvalidDataException">文件 SHA-256 与服务端不匹配（触发重传）。</exception>
    public async Task<DownloadResult?> DownloadAsync(string remotePath, string localPath, IProgress<long>? progress = null, CancellationToken ct = default)
    {
        string url = $"{SpecRoutes.FilesDownload}?path={Uri.EscapeDataString(remotePath)}";
        var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        string? lastModified = response.Headers.TryGetValues("X-File-Modified", out var values)
            ? values.FirstOrDefault() : null;

        string? expectedHash = response.Headers.TryGetValues("X-File-Hash", out var hashValues)
            ? hashValues.FirstOrDefault() : null;

        string? dir = Path.GetDirectoryName(localPath);
        if (dir != null)
        {
            Directory.CreateDirectory(dir);
        }

        string tmpPath = localPath + ".tmp";
        await using (var rawStream = await response.Content.ReadAsStreamAsync(ct))
        {
            Stream downloadStream = rawStream;
            long downloadLimit = Interlocked.Read(ref _downloadLimitBps); // T-063：运行时可变，每次传输读当前值
            if (downloadLimit > 0)
            {
                downloadStream = new ThrottledStream(rawStream, downloadLimit);
            }

            await using (downloadStream)
            await using (var fileStream = File.Create(tmpPath))
            {
                await downloadStream.CopyToAsync(fileStream, ct);
            }
        }

        // 下载后 SHA-256 校验（与 shared-spec.json §5 对齐）
        if (!string.IsNullOrEmpty(expectedHash))
        {
            string actualHash = await FileHasher.ComputeSha256Async(tmpPath, ct);
            if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                SafeDelete(tmpPath);
                throw new InvalidDataException(
                    $"下载校验失败: {remotePath}。期望哈希: {expectedHash[..16]}..., 实际: {actualHash[..16]}...");
            }
        }

        // 原子替换（同卷 Move+overwrite 是原子的）
        File.Move(tmpPath, localPath, overwrite: true);

        return new DownloadResult { LastModified = lastModified, ExpectedHash = expectedHash };
    }

    /// <summary>安全删除文件，不抛异常。</summary>
    private void SafeDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "删除临时文件失败: {Path}", path);
        }
    }

    /// <summary>删除文件。</summary>
    public async Task DeleteAsync(string path, int baseVersion, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync(SpecRoutes.FilesDelete,
            new { path, baseVersion }, JsonOptions, ct);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>移动/重命名文件。</summary>
    public async Task MoveAsync(string oldPath, string newPath, int baseVersion, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync(SpecRoutes.FilesMove,
            new { oldPath, newPath, baseVersion }, JsonOptions, ct);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>创建文件夹。</summary>
    public async Task MkdirAsync(string path, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync(SpecRoutes.FilesMkdir,
            new { path }, JsonOptions, ct);
        response.EnsureSuccessStatusCode();
    }

    public void Dispose() => _http.Dispose();
}
