using System.Net.Http.Json;
using System.Text.Json;
using CloudPan.Contract;
using Microsoft.Extensions.Logging;

namespace CloudPan.Client.Core.Services;

/// <summary>ApiClient 部分类：分块上传（阈值与块大小来自 shared-spec.json → SpecConfig）。</summary>
public partial class ApiClient
{
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
        // 服务端当前版本号：isComplete 恢复路径（全块已收）下写入快照用，避免兜底 version=0 引发整文件无谓重下载（T-064）
        int serverVersion = status?.Data?.Version ?? 0;

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
        // 兜底填服务端当前版本而非 0：快照不被置 0，避免下轮同步将整文件视为已变更而重复下载（T-064）
        return new UploadResponse(new UploadData(remotePath, serverVersion, fileHash, fileSize, false));
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
}
