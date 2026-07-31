using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using CloudPan.Shared;
using Microsoft.Extensions.Logging;

namespace CloudPan.Client.Services;

/// <summary>
/// 服务端 HTTP API 客户端。
/// 支持 Bearer Token 认证、X-Device-Id 设备标识、直传与分块上传。
/// Phase 0 对自签证书静默接受（ServerCertificateCustomValidationCallback 始终返回 true）。
/// </summary>
public class ApiClient : IApiClient, IDisposable
{
    private readonly HttpClient _http;
    private readonly long _uploadLimitBps;
    private readonly long _downloadLimitBps;
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
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true
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

    /// <summary>健康检查。</summary>
    public async Task<bool> HealthCheckAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await _http.GetAsync("/api/health", ct);
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

    /// <summary>获取文件树（增量）。</summary>
    public async Task<FileTreeApiResponse?> GetFileTreeAsync(int sinceVersion, int limit = 5000, string? subPath = null, string? cursor = null, CancellationToken ct = default)
    {
        string url = $"/api/files/tree?sinceVersion={sinceVersion}&limit={limit}";
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
        return await response.Content.ReadFromJsonAsync<FileTreeApiResponse>(JsonOptions, ct);
    }

    /// <summary>上传文件。</summary>
    public async Task<UploadApiResponse?> UploadAsync(
        string localPath, string remotePath, int baseVersion, string lastModified,
        IProgress<long>? progress = null, CancellationToken ct = default)
    {
        using MultipartFormDataContent form = new MultipartFormDataContent();
        Stream fileStream = File.OpenRead(localPath);
        if (_uploadLimitBps > 0)
        {
            fileStream = new ThrottledStream(fileStream, _uploadLimitBps);
        }

        StreamContent fileContent = new StreamContent(fileStream); // form 释放时自动释放 fileContent → fileStream

        form.Add(fileContent, "file", Path.GetFileName(remotePath));
        form.Add(new StringContent(remotePath), "path");
        form.Add(new StringContent(baseVersion.ToString()), "baseVersion");
        form.Add(new StringContent(lastModified), "lastModified");

        var response = await _http.PostAsync("/api/files/upload", form, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<UploadApiResponse>(JsonOptions, ct);
    }

    /// <summary>下载文件。返回服务端文件最后修改时间和期望哈希。</summary>
    /// <exception cref="InvalidDataException">文件 SHA-256 与服务端不匹配（触发重传）。</exception>
    public async Task<DownloadResult?> DownloadAsync(string remotePath, string localPath, IProgress<long>? progress = null, CancellationToken ct = default)
    {
        string url = $"/api/files/download?path={Uri.EscapeDataString(remotePath)}";
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
            if (_downloadLimitBps > 0)
            {
                downloadStream = new ThrottledStream(rawStream, _downloadLimitBps);
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
            string actualHash = await ComputeSha256Async(tmpPath, ct);
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

    /// <summary>计算文件 SHA-256（64 字符十六进制）。</summary>
    private static async Task<string> ComputeSha256Async(string filePath, CancellationToken ct = default)
    {
        using SHA256 sha = SHA256.Create();
        await using var stream = File.OpenRead(filePath);
        byte[] hash = await sha.ComputeHashAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
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
        var response = await _http.PostAsJsonAsync("/api/files/delete",
            new { path, baseVersion }, JsonOptions, ct);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>移动/重命名文件。</summary>
    public async Task MoveAsync(string oldPath, string newPath, int baseVersion, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync("/api/files/move",
            new { oldPath, newPath, baseVersion }, JsonOptions, ct);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>创建文件夹。</summary>
    public async Task MkdirAsync(string path, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync("/api/files/mkdir",
            new { path }, JsonOptions, ct);
        response.EnsureSuccessStatusCode();
    }

    // ============================================================
    // 分块上传
    // ============================================================

    private const long ChunkedUploadThreshold = 10_485_760; // 10MB
    private const int ChunkSizeBytes = 4_194_304;           // 4MB

    /// <summary>分块上传文件（自动判断 <10MB 直传、>=10MB 分块）。</summary>
    public async Task<UploadApiResponse?> UploadChunkedAsync(
        string localPath, string remotePath, int baseVersion, string lastModified,
        IProgress<long>? progress = null, CancellationToken ct = default)
    {
        long fileSize = new FileInfo(localPath).Length;

        // 小文件直传（复用现有逻辑）
        if (fileSize < ChunkedUploadThreshold)
        {
            return await UploadAsync(localPath, remotePath, baseVersion, lastModified, progress, ct);
        }

        // 大文件分块上传
        string fileHash = await ComputeSha256Async(localPath, ct);
        int totalChunks = (int)Math.Ceiling((double)fileSize / ChunkSizeBytes);

        // 查询服务端进度（断点续传）
        var status = await GetChunkStatusAsync(remotePath, ct);
        var receivedChunks = status?.Data?.ReceivedChunks ?? new List<int>();

        await using var fileStream = File.OpenRead(localPath);

        for (int i = 0; i < totalChunks; i++)
        {
            // 跳过已接收的块
            if (receivedChunks.Contains(i))
            {
                continue;
            }

            long offset = i * (long)ChunkSizeBytes;
            int currentChunkSize = (int)Math.Min(ChunkSizeBytes, fileSize - offset);

            byte[] buffer = new byte[currentChunkSize];
            fileStream.Position = offset;
            await fileStream.ReadExactlyAsync(buffer, 0, currentChunkSize, ct);

            using MultipartFormDataContent form = new MultipartFormDataContent();
            form.Add(new ByteArrayContent(buffer), "chunk", $"chunk_{i}");
            form.Add(new StringContent(remotePath), "path");
            form.Add(new StringContent(i.ToString()), "chunkIndex");
            form.Add(new StringContent(totalChunks.ToString()), "totalChunks");
            form.Add(new StringContent(fileHash), "fileHash");
            form.Add(new StringContent(baseVersion.ToString()), "baseVersion");
            form.Add(new StringContent(lastModified), "lastModified");

            var response = await _http.PostAsync("/api/files/upload/chunk", form, ct);

            // 处理冲突
            if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
            {
                string conflictJson = await response.Content.ReadAsStringAsync(ct);
                throw new HttpRequestException($"上传冲突: {conflictJson}", null, System.Net.HttpStatusCode.Conflict);
            }

            response.EnsureSuccessStatusCode();

            var chunkResult = await response.Content.ReadFromJsonAsync<ChunkApiResponse>(JsonOptions, ct);
            progress?.Report((i + 1) * 100L / totalChunks);

            // 服务端返回 complete，直接提取响应
            if (chunkResult?.Data?.Status == "complete")
            {
                return new UploadApiResponse
                {
                    Data = new UploadDataDto
                    {
                        Path = chunkResult.Data.Path ?? remotePath,
                        Version = chunkResult.Data.Version,
                        Hash = chunkResult.Data.Hash ?? fileHash,
                        Size = chunkResult.Data.Size,
                        ConflictResolved = false
                    }
                };
            }
        }

        // 所有块上传完毕（理论上服务端会在最后一块完成时返回 complete）
        return new UploadApiResponse
        {
            Data = new UploadDataDto
            {
                Path = remotePath,
                Version = 0,
                Hash = fileHash,
                Size = fileSize,
                ConflictResolved = false
            }
        };
    }

    /// <summary>查询分块上传进度。</summary>
    public async Task<ChunkStatusResponse?> GetChunkStatusAsync(string path, CancellationToken ct = default)
    {
        try
        {
            var response = await _http.GetAsync(
                $"/api/files/upload/chunk/status?path={Uri.EscapeDataString(path)}", ct);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return null;
            }

            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<ChunkApiStatusResponse>(JsonOptions, ct);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "查询分块上传进度失败（将从头开始）");
            return null; // 查询失败则从头开始
        }
    }

    // ============================================================
    // 限速流
    // ============================================================

    /// <summary>限速读取流——控制每秒读取字节数。</summary>
    private class ThrottledStream : Stream
    {
        private readonly Stream _inner;
        private readonly double _bytesPerTick;
        private long _bytesThisTick;
        private long _tickStartTicks;

        private const long TicksPerSecond = 10_000_000; // 1 tick = 100ns

        public ThrottledStream(Stream inner, long bytesPerSecond)
        {
            _inner = inner;
            _bytesPerTick = bytesPerSecond / (double)TicksPerSecond;
            _tickStartTicks = DateTime.UtcNow.Ticks;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_bytesPerTick <= 0)
            {
                return _inner.Read(buffer, offset, count);
            }

            long now = DateTime.UtcNow.Ticks;
            long elapsed = now - _tickStartTicks;

            // 每秒重置一次计数器
            if (elapsed > TicksPerSecond)
            {
                _tickStartTicks = now;
                _bytesThisTick = 0;
            }

            long maxBytes = (long)(_bytesPerTick * elapsed);
            int allowed = (int)Math.Min(count, maxBytes - _bytesThisTick);
            if (allowed <= 0) { Thread.Sleep(10); return 0; }

            int read = _inner.Read(buffer, offset, allowed);
            _bytesThisTick += read;
            return read;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            if (_bytesPerTick <= 0)
            {
                return await _inner.ReadAsync(buffer, offset, count, ct);
            }

            long now = DateTime.UtcNow.Ticks;
            long elapsed = now - _tickStartTicks;

            if (elapsed > TicksPerSecond)
            {
                _tickStartTicks = now;
                _bytesThisTick = 0;
            }

            long maxBytes = (long)(_bytesPerTick * elapsed);
            int allowed = (int)Math.Min(count, maxBytes - _bytesThisTick);
            if (allowed <= 0) { await Task.Delay(10, ct); return 0; }

            int read = await _inner.ReadAsync(buffer, offset, allowed, ct);
            _bytesThisTick += read;
            return read;
        }

        public override async Task CopyToAsync(Stream destination, int bufferSize, CancellationToken ct)
        {
            if (_bytesPerTick <= 0)
            {
                await _inner.CopyToAsync(destination, bufferSize, ct);
                return;
            }

            byte[] buffer = new byte[bufferSize];
            int bytesRead;
            while ((bytesRead = await ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
            {
                await destination.WriteAsync(buffer, 0, bytesRead, ct);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() => _inner.Flush();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    public void Dispose() => _http.Dispose();
}

// ---- API 响应 DTO（与 shared-spec/apiMapping 对齐） ----

/// <summary>文件树列表响应。</summary>
public class FileTreeApiResponse
{
    public List<FileEntryDto> Data { get; set; } = new();
    public string? NextCursor { get; set; }
    public bool HasMore { get; set; }
    public int MaxVersion { get; set; }
}

/// <summary>上传响应。</summary>
public class UploadApiResponse
{
    public UploadDataDto Data { get; set; } = new();
}

/// <summary>上传结果数据。</summary>
public class UploadDataDto
{
    public string Path { get; set; } = "";
    public int Version { get; set; }
    public string Hash { get; set; } = "";
    public long Size { get; set; }
    public bool ConflictResolved { get; set; }
}

// ---- 分块上传 DTO ----

/// <summary>分块上传响应。</summary>
public class ChunkApiResponse
{
    public ChunkApiData? Data { get; set; }
}

/// <summary>分块上传结果数据。</summary>
public class ChunkApiData
{
    public string? Path { get; set; }
    public string? Status { get; set; }    // "complete" 或 null
    public int Version { get; set; }
    public string? Hash { get; set; }
    public long Size { get; set; }
    public int ChunkIndex { get; set; }
    public int ReceivedCount { get; set; }
    public int TotalChunks { get; set; }
    public bool IsComplete { get; set; }
}

/// <summary>分块接收状态响应。</summary>
public class ChunkStatusResponse
{
    public ChunkStatusData? Data { get; set; }
}

/// <summary>分块接收状态数据。</summary>
public class ChunkStatusData
{
    public List<int> ReceivedChunks { get; set; } = new();
    public int TotalChunks { get; set; }
    public bool IsComplete { get; set; }
    public string? FilePath { get; set; }
    public string? DeviceId { get; set; }
    public string? CreatedAt { get; set; }
}

/// <summary>用于反序列化 chunk/status 响应的中间类。</summary>
public class ChunkApiStatusResponse : ChunkStatusResponse { }

/// <summary>下载结果——包含服务端最后修改时间和 X-File-Hash 期望哈希值。</summary>
public class DownloadResult
{
    public string? LastModified { get; set; }
    public string? ExpectedHash { get; set; }
}
