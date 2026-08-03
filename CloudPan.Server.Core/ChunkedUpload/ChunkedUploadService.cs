using System.Text.Json;
using CloudPan.Contract;
using CloudPan.Infrastructure.Models;
using CloudPan.Infrastructure.Persistence;
using CloudPan.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;

namespace CloudPan.Server.Core;

/// <inheritdoc />
public partial class ChunkedUploadService : IChunkedUploadService
{
    private const int ChunkedUploadTimeoutMinutes = SpecConfig.ChunkedUploadTimeoutMinutes; // 单源：shared-spec.json → SpecConfig

    private readonly IDbContextFactory<CloudPanDbContext> _dbFactory;
    private readonly IFileStorageService _storage;
    private readonly IFileIndexService _index;
    private readonly IVersionService _version;
    private readonly ISyncLogService _syncLog;

    public ChunkedUploadService(
        IDbContextFactory<CloudPanDbContext> dbFactory,
        IFileStorageService storage,
        IFileIndexService index,
        IVersionService version,
        ISyncLogService syncLog)
    {
        _dbFactory = dbFactory;
        _storage = storage;
        _index = index;
        _version = version;
        _syncLog = syncLog;
    }

    /// <inheritdoc />
    public async Task<ChunkUploadOutcome> ReceiveChunkAsync(
        string path, int chunkIndex, int totalChunks, string fileHash,
        int baseVersion, string? lastModified, string deviceId, Stream chunkContent)
    {
        // 路径安全统一防线
        string? pathErr = _storage.ValidatePath(path);
        if (pathErr != null)
        {
            return new ChunkErrorOutcome(new DomainError(HttpErrorCode.BAD_REQUEST, pathErr, "路径格式不正确"));
        }

        await using var db = await _dbFactory.CreateDbContextAsync();

        // 查找或创建 ChunkedUpload 记录
        ChunkedUpload? record;

        if (chunkIndex == 0)
        {
            // 清理超时的旧记录 + 临时文件
            string expiryTime = DateTime.UtcNow.AddMinutes(-ChunkedUploadTimeoutMinutes).ToString("O");
            var stale = await db.ChunkedUploads
                .Where(c => c.FilePath == path && string.Compare(c.CreatedAt, expiryTime) < 0)
                .ToListAsync();
            foreach (var s in stale)
            {
                SafeDeleteTemp(s.TempPath);
                db.ChunkedUploads.Remove(s);
            }

            // 检查是否已有同设备同路径的上传记录
            record = await db.ChunkedUploads.FindAsync(path);
            if (record != null)
            {
                if (record.DeviceId != deviceId)
                {
                    return new ChunkErrorOutcome(new DomainError(HttpErrorCode.CONFLICT,
                        "另一设备正在上传该文件", "该文件正在被其他设备上传，请稍后重试"));
                }
                // 同一设备：断点续传，重置数据（同时删除旧临时文件，避免旧数据污染合并结果）
                record.TotalChunks = totalChunks;
                record.FileHash = fileHash;
                record.BaseVersion = baseVersion;
                record.LastModified = lastModified ?? DateTime.UtcNow.ToString("O");
                record.ReceivedChunks = "[]";
                record.CreatedAt = DateTime.UtcNow.ToString("O");
                SafeDeleteTemp(record.TempPath);
            }
            else
            {
                // 创建临时文件
                string tempDir = Path.Combine(
                    Path.GetDirectoryName(_storage.GetAbsolutePath(path))!,
                    ".cloudpan");
                Directory.CreateDirectory(tempDir);
                string tempPath = Path.Combine(tempDir, $"{Guid.NewGuid():N}.chunk.tmp");

                record = new ChunkedUpload
                {
                    FilePath = path,
                    DeviceId = deviceId,
                    FileHash = fileHash,
                    TotalChunks = totalChunks,
                    ReceivedChunks = "[]",
                    TempPath = tempPath,
                    BaseVersion = baseVersion,
                    LastModified = lastModified ?? DateTime.UtcNow.ToString("O"),
                    CreatedAt = DateTime.UtcNow.ToString("O")
                };
                db.ChunkedUploads.Add(record);
            }
            await db.SaveChangesAsync();
        }
        else
        {
            record = await db.ChunkedUploads.FindAsync(path);
            if (record == null)
            {
                return new ChunkErrorOutcome(new DomainError(HttpErrorCode.BAD_REQUEST,
                    "分块上传会话不存在，请先传 chunkIndex=0", "分块上传会话已过期，请重新上传"));
            }

            if (record.TotalChunks != totalChunks)
            {
                return new ChunkErrorOutcome(new DomainError(HttpErrorCode.BAD_REQUEST,
                    "totalChunks 与首块不一致", "分块参数与首次上传不一致"));
            }

            if (!string.Equals(record.FileHash, fileHash, StringComparison.OrdinalIgnoreCase))
            {
                return new ChunkErrorOutcome(new DomainError(HttpErrorCode.BAD_REQUEST,
                    "fileHash 与首块不一致", "文件校验信息与首次上传不一致"));
            }
        }

        // 解析已接收块列表
        var received = JsonSerializer.Deserialize<List<int>>(record.ReceivedChunks)
                       ?? new List<int>();

        // 幂等：已接收则跳过
        if (received.Contains(chunkIndex))
        {
            return new ChunkProgressOutcome(path, chunkIndex, received.Count, totalChunks,
                received.Count == totalChunks);
        }

        // 写入块数据（按块索引 seek 定位写入：块 i 固定落在 [i*ChunkSize, (i+1)*ChunkSize) 区间，与客户端切块语义一致）
        // 崩溃恢复幂等：若字节已落盘但位图未更新（两步间崩溃），客户端重发同块时覆盖同位置而非追加 → 不产生重复字节，
        // 合并后 SHA-256 必然与完整文件一致。保持『先落字节、后更位图』顺序：唯一崩溃窗口被 seek 覆盖收敛。
        long chunkOffset = (long)chunkIndex * SpecConfig.ChunkSize;
        await using (FileStream fs = new FileStream(record.TempPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None))
        {
            fs.Seek(chunkOffset, SeekOrigin.Begin);
            await chunkContent.CopyToAsync(fs);
            await fs.FlushAsync();
        }

        received.Add(chunkIndex);
        record.ReceivedChunks = JsonSerializer.Serialize(received);
        await db.SaveChangesAsync();

        // 判断是否最后一块
        if (received.Count == totalChunks)
        {
            return await FinalizeAsync(db, record, path, fileHash, baseVersion, lastModified, deviceId);
        }

        return new ChunkProgressOutcome(path, chunkIndex, received.Count, totalChunks, false);
    }

    /// <inheritdoc />
    public async Task<ChunkStatusResult> GetStatusAsync(string path)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var record = await db.ChunkedUploads.FindAsync(path);

        if (record == null)
        {
            return new ChunkStatusResult(false, null, Array.Empty<int>(), 0, false, null, null);
        }

        var received = JsonSerializer.Deserialize<List<int>>(record.ReceivedChunks) ?? new List<int>();
        return new ChunkStatusResult(true, record.FilePath, received, record.TotalChunks,
            received.Count == record.TotalChunks, record.DeviceId, record.CreatedAt);
    }
}
