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
        if (_uploadLimitBps > 0)
        {
            fileStream = new ThrottledStream(fileStream, _uploadLimitBps);
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

    // ============================================================
    // 回收站（/api/trash，T-014：客户端删除进回收站 + 恢复/撤销）
    // ============================================================

    /// <summary>获取回收站列表（按删除时间倒序）。</summary>
    public async Task<List<TrashItem>> GetTrashAsync(CancellationToken ct = default)
    {
        var response = await _http.GetAsync(SpecRoutes.Trash, ct);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<TrashListResponse>(JsonOptions, ct);
        return result?.Data?.ToList() ?? new List<TrashItem>();
    }

    /// <summary>恢复回收站条目到原位（撤销删除）。</summary>
    public async Task RestoreTrashAsync(string metaFileName, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync(SpecRoutes.TrashRestore,
            new { metaFileName }, JsonOptions, ct);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>清空回收站。</summary>
    public async Task EmptyTrashAsync(CancellationToken ct = default)
    {
        var response = await _http.DeleteAsync(SpecRoutes.TrashEmpty, ct);
        response.EnsureSuccessStatusCode();
    }

    // ============================================================
    // 分享与版本历史（/api/shares + /api/versions，T-018：客户端 UI 入口）
    // ============================================================

    /// <summary>创建分享链接。expiresAt 传 ISO 8601 UTC（如 DateTime.UtcNow.AddDays(7).ToString("O")），null 表示永不过期。</summary>
    public async Task<ShareCreateResponse?> CreateShareAsync(
        string filePath, string? password, string? expiresAt, int? maxDownloads, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync(SpecRoutes.Shares,
            new { filePath, password, expiresAt, maxDownloads }, JsonOptions, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ShareCreateResponse>(JsonOptions, ct);
    }

    /// <summary>撤销分享链接。返回 false 表示分享不存在或已失效。</summary>
    public async Task<bool> RevokeShareAsync(string shareId, CancellationToken ct = default)
    {
        var response = await _http.DeleteAsync(
            SpecRoutes.SharesByShareId.Replace("{shareId}", Uri.EscapeDataString(shareId)), ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }

        response.EnsureSuccessStatusCode();
        return true;
    }

    /// <summary>获取文件历史版本列表（按版本倒序，上限 limit）。</summary>
    public async Task<List<VersionItem>> GetVersionsAsync(string path, int limit = 50, CancellationToken ct = default)
    {
        var response = await _http.GetAsync(
            $"{SpecRoutes.Versions}?path={Uri.EscapeDataString(path)}&limit={limit}", ct);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<VersionListResponse>(JsonOptions, ct);
        return result?.Data?.ToList() ?? new List<VersionItem>();
    }

    /// <summary>回滚文件到指定历史版本（服务端会先存档当前版本，再用历史文件覆盖）。</summary>
    public async Task<VersionRestoreResponse?> RestoreVersionAsync(string filePath, int version, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync(SpecRoutes.VersionsRestore,
            new { filePath, version }, JsonOptions, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<VersionRestoreResponse>(JsonOptions, ct);
    }

    // ============================================================
    // 分块上传（阈值与块大小来自 shared-spec.json → SpecConfig，改 spec 一处生效）
    // ============================================================

    /// <summary>分块上传文件（自动判断 < 阈值直传、≥ 阈值分块）。</summary>
    public async Task<UploadResponse?> UploadChunkedAsync(
        string localPath, string remotePath, int baseVersion, string lastModified,
        IProgress<long>? progress = null, CancellationToken ct = default)
    {
        long fileSize = new FileInfo(localPath).Length;

        // 小文件直传（复用现有逻辑）
        if (fileSize < SpecConfig.ChunkedUploadThreshold)
        {
            return await UploadAsync(localPath, remotePath, baseVersion, lastModified, progress, ct);
        }

        // 大文件分块上传
        string fileHash = await FileHasher.ComputeSha256Async(localPath, ct);
        int totalChunks = (int)Math.Ceiling((double)fileSize / SpecConfig.ChunkSize);

        // 查询服务端进度（断点续传）
        var status = await GetChunkStatusAsync(remotePath, ct);
        var receivedChunks = status?.Data?.ReceivedChunks ?? Array.Empty<int>();

        await using var fileStream = File.OpenRead(localPath);

        for (int i = 0; i < totalChunks; i++)
        {
            // 跳过已接收的块
            if (receivedChunks.Contains(i))
            {
                continue;
            }

            long offset = i * (long)SpecConfig.ChunkSize;
            int currentChunkSize = (int)Math.Min(SpecConfig.ChunkSize, fileSize - offset);

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

            var response = await _http.PostAsync(SpecRoutes.FilesUploadChunk, form, ct);

            // 处理冲突
            if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
            {
                string conflictJson = await response.Content.ReadAsStringAsync(ct);
                throw new HttpRequestException($"上传冲突: {conflictJson}", null, System.Net.HttpStatusCode.Conflict);
            }

            response.EnsureSuccessStatusCode();

            var chunkResult = await response.Content.ReadFromJsonAsync<ChunkUploadResponse>(JsonOptions, ct);
            progress?.Report((i + 1) * 100L / totalChunks);

            // 服务端返回 complete，直接提取响应
            if (chunkResult?.Data?.Status == "complete")
            {
                return new UploadResponse(new UploadData(
                    chunkResult.Data.Path,
                    chunkResult.Data.Version,
                    chunkResult.Data.Hash ?? fileHash,
                    chunkResult.Data.Size,
                    false));
            }
        }

        // 所有块上传完毕（理论上服务端会在最后一块完成时返回 complete）
        return new UploadResponse(new UploadData(remotePath, 0, fileHash, fileSize, false));
    }

    /// <summary>查询分块上传进度。</summary>
    public async Task<ChunkStatusResponse?> GetChunkStatusAsync(string path, CancellationToken ct = default)
    {
        try
        {
            var response = await _http.GetAsync(
                $"{SpecRoutes.FilesUploadChunkStatus}?path={Uri.EscapeDataString(path)}", ct);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return null;
            }

            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<ChunkStatusResponse>(JsonOptions, ct);
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

/// <summary>下载结果——包含服务端最后修改时间和 X-File-Hash 期望哈希值。</summary>
public class DownloadResult
{
    public string? LastModified { get; set; }
    public string? ExpectedHash { get; set; }
}
