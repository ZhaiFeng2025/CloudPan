using System.Security.Cryptography;
using System.Text;
using CloudPan.Infrastructure.Storage;
using CloudPan.Server.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CloudPan.Tests.Server.Services;

/// <summary>
/// ChunkedUploadService 单元测试——分块接收、幂等跳过、合并完成（脱离 ASP.NET，直接注入领域服务）。
/// </summary>
public class ChunkedUploadServiceTests : Infrastructure.TestBase
{
    private ChunkedUploadService CreateServiceAsync()
    {
        var dbFactory = CreateServerDbFactory();
        var storage = new FileStorageService(TempDir);
        var index = new FileIndexService(dbFactory);
        var version = new VersionService(dbFactory);
        var syncLog = new SyncLogService(dbFactory, NullLogger<SyncLogService>.Instance);
        return new ChunkedUploadService(dbFactory, storage, index, version, syncLog);
    }

    [Fact]
    public async Task ReceiveChunk_两块_完成后合并文件一致()
    {
        var svc = CreateServiceAsync();
        string full = "chunk upload test content here";
        byte[] bytes = Encoding.UTF8.GetBytes(full);
        string hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        int mid = bytes.Length / 2;

        using var s1 = new MemoryStream(bytes[..mid]);
        var out1 = await svc.ReceiveChunkAsync("/upload.bin", 0, 2, hash, 0, null, "dev-1", s1);
        Assert.IsType<ChunkProgressOutcome>(out1);

        using var s2 = new MemoryStream(bytes[mid..]);
        var out2 = await svc.ReceiveChunkAsync("/upload.bin", 1, 2, hash, 0, null, "dev-1", s2);
        var completed = Assert.IsType<ChunkCompletedOutcome>(out2);
        Assert.Equal(hash, completed.Hash);
        Assert.Equal(bytes.Length, completed.Size);

        string merged = await File.ReadAllTextAsync(Path.Combine(TempDir, "upload.bin"));
        Assert.Equal(full, merged);
    }

    [Fact]
    public async Task ReceiveChunk_重复非首块_幂等跳过()
    {
        var svc = CreateServiceAsync();
        byte[] bytes = Encoding.UTF8.GetBytes("idempotent test content");
        string hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        int third = bytes.Length / 3;

        using var s0 = new MemoryStream(bytes[..third]);
        await svc.ReceiveChunkAsync("/idem.bin", 0, 3, hash, 0, null, "dev-1", s0);
        using var s1 = new MemoryStream(bytes[third..(third * 2)]);
        await svc.ReceiveChunkAsync("/idem.bin", 1, 3, hash, 0, null, "dev-1", s1);

        // 重发 chunk 1（非首块）→ 已接收，跳过（receivedCount 仍为 2）
        using var dup = new MemoryStream(bytes[third..(third * 2)]);
        var outcome = await svc.ReceiveChunkAsync("/idem.bin", 1, 3, hash, 0, null, "dev-1", dup);

        var progress = Assert.IsType<ChunkProgressOutcome>(outcome);
        Assert.Equal(2, progress.ReceivedCount);
    }

    [Fact]
    public async Task ReceiveChunk_哈希不匹配_返回错误()
    {
        var svc = CreateServiceAsync();
        byte[] bytes = Encoding.UTF8.GetBytes("some bytes");
        string wrongHash = new string('0', 64);

        using var s1 = new MemoryStream(bytes);
        var outcome = await svc.ReceiveChunkAsync("/bad.bin", 0, 1, wrongHash, 0, null, "dev-1", s1);

        Assert.IsType<ChunkErrorOutcome>(outcome);
    }

    [Fact]
    public async Task GetStatus_无会话_返回Found为false()
    {
        var svc = CreateServiceAsync();

        var status = await svc.GetStatusAsync("/never-started.bin");

        Assert.False(status.Found);
    }

    [Fact]
    public async Task GetStatus_有会话_返回进度()
    {
        var svc = CreateServiceAsync();
        byte[] bytes = Encoding.UTF8.GetBytes("status check");
        string hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        using var s1 = new MemoryStream(bytes);
        await svc.ReceiveChunkAsync("/status.bin", 0, 2, hash, 0, null, "dev-1", s1);

        var status = await svc.GetStatusAsync("/status.bin");

        Assert.True(status.Found);
        Assert.Single(status.ReceivedChunks!);
        Assert.Equal(2, status.TotalChunks);
        Assert.False(status.IsComplete);
    }
}
