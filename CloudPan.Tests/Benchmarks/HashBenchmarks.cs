using System.Security.Cryptography;
using BenchmarkDotNet.Attributes;
using CloudPan.Server.Services;

namespace CloudPan.Tests.Benchmarks;

/// <summary>
/// SHA-256 计算与原子写入（tmp → 校验 → rename）性能基准。
/// 基准方法直接调用 FileStorageService 生产代码路径，反映真实同步开销。
/// 运行方式：dotnet run -c Release --project CloudPan.Tests
/// </summary>
[SimpleJob]
[MemoryDiagnoser]
public class HashBenchmarks
{
    /// <summary>数据块大小（字节）：1KB / 1MB / 16MB</summary>
    [Params(1_024, 1_048_576, 16_777_216)]
    public int DataSize { get; set; }

    private byte[] _data = [];
    private string _expectedHash = "";
    private string _tempDir = "";
    private FileStorageService? _storage;

    [GlobalSetup]
    public void Setup()
    {
        _data = new byte[DataSize];
        Random.Shared.NextBytes(_data);
        _expectedHash = Convert.ToHexString(SHA256.HashData(_data)).ToLowerInvariant();

        _tempDir = Path.Combine(Path.GetTempPath(), "CloudPanBench_Hash_" + Guid.NewGuid().ToString("N"));
        _storage = new FileStorageService(_tempDir);
        _storage.EnsureSyncRootExists();
        File.WriteAllBytes(_storage.GetAbsolutePath("/hash_target.bin"), _data);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // 清理失败不影响基准结果
        }
    }

    /// <summary>内存中 SHA-256（静态 API，性能参考下限）</summary>
    [Benchmark]
    public byte[] Sha256InMemory() => SHA256.HashData(_data);

    /// <summary>文件 SHA-256（生产路径 FileStorageService.ComputeHashAsync）</summary>
    [Benchmark]
    public async Task<string> Sha256File()
        => await _storage!.ComputeHashAsync(_storage.GetAbsolutePath("/hash_target.bin"));

    /// <summary>原子写入：写 .tmp → 校验哈希 → rename（生产路径 AtomicWriteAsync）</summary>
    [Benchmark]
    public async Task AtomicWrite()
    {
        using var stream = new MemoryStream(_data, writable: false);
        string? error = await _storage!.AtomicWriteAsync("/bench.bin", stream, _expectedHash);
        if (error != null)
        {
            throw new InvalidOperationException(error);
        }
    }
}
