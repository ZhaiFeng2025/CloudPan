using CloudPan.Server.Services;
using Xunit;

namespace CloudPan.Tests.Server.Services;

/// <summary>
/// FileStorageService 单元测试——验证路径验证、原子写入、哈希计算。
/// </summary>
public class FileStorageServiceTests : Infrastructure.TestBase
{
    [Fact]
    public void ValidatePath_合法路径_返回null()
    {
        FileStorageService svc = new FileStorageService(TempDir);
        string? result = svc.ValidatePath("/docs/report.docx");
        Assert.Null(result);
    }

    [Fact]
    public void ValidatePath_空字符串_返回错误()
    {
        FileStorageService svc = new FileStorageService(TempDir);
        string? result = svc.ValidatePath("");
        Assert.NotNull(result);
        Assert.Contains("不能为空", result);
    }

    [Fact]
    public void ValidatePath_含点点_返回错误_路径遍历攻击()
    {
        FileStorageService svc = new FileStorageService(TempDir);
        string? result = svc.ValidatePath("/../../../etc/passwd");
        Assert.NotNull(result);
        Assert.Contains("越界", result);
    }

    [Fact]
    public void ValidatePath_含空字符_返回错误()
    {
        FileStorageService svc = new FileStorageService(TempDir);
        string? result = svc.ValidatePath("/test\0bad");
        Assert.NotNull(result);
        Assert.Contains("空字符", result);
    }

    [Fact]
    public void ValidatePath_路径越界_返回错误()
    {
        FileStorageService svc = new FileStorageService(TempDir);
        string? result = svc.ValidatePath("/../outside.txt");
        Assert.NotNull(result);
    }

    [Fact]
    public void GetAbsolutePath_去除开头的斜杠()
    {
        FileStorageService svc = new FileStorageService(TempDir);
        string result = svc.GetAbsolutePath("/docs/file.txt");
        // 使用 Path.GetFullPath 规范化路径分隔符
        string expected = Path.GetFullPath(Path.Combine(TempDir, "docs", "file.txt"));
        Assert.Equal(expected, Path.GetFullPath(result));
    }

    [Fact]
    public async Task AtomicWrite_正常写入_文件存在且内容正确()
    {
        FileStorageService svc = new FileStorageService(TempDir);
        byte[] content = "Hello CloudPan!"u8.ToArray();
        using MemoryStream stream = new MemoryStream(content);

        string? error = await svc.AtomicWriteAsync("/test/hello.txt", stream, expectedHash: null);
        Assert.Null(error);

        string fullPath = Path.Combine(TempDir, "test", "hello.txt");
        Assert.True(File.Exists(fullPath));

        byte[] written = await File.ReadAllBytesAsync(fullPath);
        Assert.Equal(content, written);
    }

    [Fact]
    public async Task AtomicWrite_自动创建父目录()
    {
        FileStorageService svc = new FileStorageService(TempDir);
        using MemoryStream stream = new MemoryStream("deep"u8.ToArray());

        await svc.AtomicWriteAsync("/a/b/c/d/file.txt", stream, expectedHash: null);

        string fullPath = Path.Combine(TempDir, "a", "b", "c", "d", "file.txt");
        Assert.True(File.Exists(fullPath));
    }

    [Fact]
    public async Task AtomicWrite_哈希校验失败_返回错误并清理tmp()
    {
        FileStorageService svc = new FileStorageService(TempDir);
        using MemoryStream stream = new MemoryStream("content"u8.ToArray());

        string? error = await svc.AtomicWriteAsync("/test/badhash.txt", stream,
            expectedHash: "0000000000000000000000000000000000000000000000000000000000000000");

        Assert.NotNull(error);
        Assert.Contains("哈希校验失败", error);

        // 确认 .tmp 已清理
        string tmpPath = Path.Combine(TempDir, "test", "badhash.txt.tmp");
        Assert.False(File.Exists(tmpPath));
    }

    [Fact]
    public async Task AtomicWrite_目标文件被锁定_返回友好错误并清理tmp()
    {
        FileStorageService svc = new FileStorageService(TempDir);
        string fullPath = Path.Combine(TempDir, "locked.txt");

        // 持有排他锁模拟文件被另一进程占用（杀毒/共享目录观察者）
        using (FileStream lockHandle = new FileStream(fullPath, FileMode.OpenOrCreate,
                   FileAccess.ReadWrite, FileShare.None))
        {
            using MemoryStream stream = new MemoryStream("content"u8.ToArray());
            string? error = await svc.AtomicWriteAsync("/locked.txt", stream, expectedHash: null);

            // 不抛异常，返回友好错误（而非 500 未处理异常）
            Assert.NotNull(error);
            Assert.Contains("被占用", error);
        }

        // 临时文件已清理
        string tmpPath = Path.Combine(TempDir, "locked.txt.tmp");
        Assert.False(File.Exists(tmpPath));
    }

    [Fact]
    public async Task AtomicWrite_锁释放后重试_成功写入()
    {
        FileStorageService svc = new FileStorageService(TempDir);
        string fullPath = Path.Combine(TempDir, "retry.txt");

        // 短暂持锁（让首次 Move 失败），随后释放，验证退避重试最终成功
        using (FileStream lockHandle = new FileStream(fullPath, FileMode.OpenOrCreate,
                   FileAccess.ReadWrite, FileShare.None))
        {
            // 持锁 300ms——首次重试（200ms）时仍被锁，第二次重试（400ms）时锁已释放
            await Task.Delay(300);
        }

        using MemoryStream stream = new MemoryStream("retry ok"u8.ToArray());
        string? error = await svc.AtomicWriteAsync("/retry.txt", stream, expectedHash: null);
        Assert.Null(error);

        byte[] written = await File.ReadAllBytesAsync(fullPath);
        Assert.Equal("retry ok"u8.ToArray(), written);
    }

    [Fact]
    public async Task ComputeHash_相同内容_相同哈希()
    {
        FileStorageService svc = new FileStorageService(TempDir);
        string filePath = Path.Combine(TempDir, "hash_test.bin");
        byte[] content = new byte[] { 1, 2, 3, 4, 5 };
        await File.WriteAllBytesAsync(filePath, content);

        string hash1 = await svc.ComputeHashAsync(filePath);
        string hash2 = await svc.ComputeHashAsync(filePath);

        Assert.Equal(hash1, hash2);
        Assert.Equal(64, hash1.Length); // SHA-256: 64 hex chars
    }

    [Fact]
    public void Exists_文件存在_返回true()
    {
        FileStorageService svc = new FileStorageService(TempDir);
        string filePath = Path.Combine(TempDir, "exists_test.txt");
        File.WriteAllText(filePath, "test");

        Assert.True(svc.Exists("/exists_test.txt"));
        Assert.False(svc.Exists("/nonexistent.txt"));
    }

    [Fact]
    public void Delete_删除文件()
    {
        FileStorageService svc = new FileStorageService(TempDir);
        string relPath = "/delete_me.txt";
        File.WriteAllText(Path.Combine(TempDir, "delete_me.txt"), "bye");

        svc.Delete(relPath);
        Assert.False(File.Exists(Path.Combine(TempDir, "delete_me.txt")));
    }

    [Fact]
    public void DeleteDirectory_递归删除()
    {
        FileStorageService svc = new FileStorageService(TempDir);
        string dir = Path.Combine(TempDir, "folder", "sub");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "file.txt"), "inside");

        svc.DeleteDirectory("/folder/");

        Assert.False(Directory.Exists(Path.Combine(TempDir, "folder")));
    }

    [Fact]
    public void Move_重命名文件()
    {
        FileStorageService svc = new FileStorageService(TempDir);
        File.WriteAllText(Path.Combine(TempDir, "old.txt"), "move me");

        svc.Move("/old.txt", "/new.txt");

        Assert.False(File.Exists(Path.Combine(TempDir, "old.txt")));
        Assert.True(File.Exists(Path.Combine(TempDir, "new.txt")));
    }

    [Fact]
    public void EnsureSyncRootExists_创建目录()
    {
        FileStorageService svc = new FileStorageService(TempDir);
        svc.EnsureSyncRootExists();
        Assert.True(Directory.Exists(Path.Combine(TempDir, ".cloudpan")));
    }
}
