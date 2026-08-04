using BenchmarkDotNet.Attributes;
using CloudPan.Infrastructure.Storage;

namespace CloudPan.Tests.Benchmarks;

/// <summary>
/// 路径验证（GetFullPath + StartsWith）与文件 SHA-256（不同大小）性能基准。
/// 运行方式：dotnet run -c Release --project CloudPan.Tests
/// </summary>
[SimpleJob]
[MemoryDiagnoser]
public class PathValidationBenchmarks
{
    /// <summary>被测相对路径：合法路径 / 目录遍历攻击路径</summary>
    [Params("docs/report.docx", "/docs/../../secret.txt")]
    public string RelativePath { get; set; } = "";

    /// <summary>哈希目标文件大小（字节）：1KB / 1MB / 16MB</summary>
    [Params(1_024, 1_048_576, 16_777_216)]
    public int FileSize { get; set; }

    private string _tempDir = "";
    private FileStorageService? _storage;

    [GlobalSetup]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "CloudPanBench_Path_" + Guid.NewGuid().ToString("N"));
        _storage = new FileStorageService(_tempDir);
        _storage.EnsureSyncRootExists();

        var bytes = new byte[FileSize];
        Random.Shared.NextBytes(bytes);
        File.WriteAllBytes(_storage.GetAbsolutePath("/hash_target.bin"), bytes);
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

    /// <summary>路径验证：GetFullPath 规范化 + 根前缀 StartsWith 检查（生产路径 ValidatePath）</summary>
    [Benchmark]
    public string? ValidatePath() => _storage!.ValidatePath(RelativePath);

    /// <summary>文件 SHA-256 计算（生产路径 FileHasher）</summary>
    [Benchmark]
    public async Task<string> HashFile()
        => await FileHasher.ComputeSha256Async("/hash_target.bin");
}
