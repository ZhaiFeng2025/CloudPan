using System.Reflection;
using CloudPan.Client.Core.Services;
using Xunit;

namespace CloudPan.Tests.Client.Services;

/// <summary>
/// ApiClient.ThrottledStream 单测（T-073）——验证限速流配额边界不把『配额等待』当 EOF 截断。
/// ThrottledStream 是 ApiClient 的 private 嵌套类，经反射构造并以其 public 覆盖方法（Stream 基类型）驱动。
/// </summary>
public class ThrottledStreamTests
{
    /// <summary>反射构造 CloudPan.Client.Core 的 ThrottledStream（internal 类型，单测经反射访问）。</summary>
    private static Stream CreateThrottled(Stream inner, long bytesPerSecond)
    {
        Type type = typeof(ApiClient).Assembly.GetType("CloudPan.Client.Core.Services.ThrottledStream")
            ?? throw new InvalidOperationException("未找到 ThrottledStream 类型");
        ConstructorInfo ctor = type.GetConstructor(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            new[] { typeof(Stream), typeof(long) },
            modifiers: null)
            ?? throw new InvalidOperationException("未找到 ThrottledStream(Stream, long) 构造器");
        return (Stream)ctor.Invoke(new object[] { inner, bytesPerSecond });
    }

    /// <summary>
    /// 限速流读取 1MB 文件完整到达且内容一致（配额边界不截断）。
    /// 512KB/s：1MB 需约 2 秒且 < 1MB/s，保证文件大于单秒配额，同一 tick 内连续读取必触发 allowed&lt;=0。
    /// 修复前第二次 Read 返回 0 被 CopyToAsync 当 EOF 直接截断。
    /// </summary>
    [Fact]
    public async Task ThrottledStream_读取1MB文件_完整到达且内容一致()
    {
        byte[] source = new byte[1024 * 1024];
        new Random(42).NextBytes(source);

        using var inner = new MemoryStream(source);
        using Stream throttled = CreateThrottled(inner, bytesPerSecond: 512 * 1024);
        using var dest = new MemoryStream();

        await throttled.CopyToAsync(dest);

        Assert.Equal(source.Length, dest.Length);
        Assert.Equal(source, dest.ToArray());
    }

    /// <summary>
    /// 同一 tick 内连续多次调用不截断：10KB/s 下首个 tick 配额极小，逐次 ReadAsync 必然触碰配额耗尽；
    /// 手动模拟 CopyToAsync 循环，断言在底层流真正 EOF 前 ReadAsync 永不返回 0。
    /// </summary>
    [Fact]
    public async Task ThrottledStream_同一tick内连续Read_配额耗尽不返回0不截断()
    {
        byte[] source = new byte[16 * 1024];
        new Random(7).NextBytes(source);

        using var inner = new MemoryStream(source);
        using Stream throttled = CreateThrottled(inner, bytesPerSecond: 10 * 1024);

        using var collected = new MemoryStream();
        byte[] buffer = new byte[4096];
        int total = 0;
        int reads = 0;
        while (true)
        {
            int n = await throttled.ReadAsync(buffer, 0, buffer.Length);
            reads++;
            if (n == 0) { break; } // 仅底层流 EOF 返回 0
            collected.Write(buffer, 0, n);
            total += n;
        }

        Assert.Equal(source.Length, total);          // 完整到达，无截断
        Assert.Equal(source, collected.ToArray());   // 内容一致
        Assert.True(reads > 1, "应经历多次读取（跨配额边界）");
    }
}
