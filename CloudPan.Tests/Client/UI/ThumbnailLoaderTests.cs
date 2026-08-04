using System.Runtime.ExceptionServices;
using System.Windows.Forms;
using CloudPan.Client.UI;
using Xunit;

namespace CloudPan.Tests.Client.UI;

/// <summary>
/// 缩略图本地缓存回收单测（T-104）：
/// 过期缓存按保留期（对齐服务端 T-088 30 天）清理、边界保留、目录缺失/非 jpg 不误删，
/// 以及 ScheduleCacheReclaim 后台回收端到端触发。
/// </summary>
public class ThumbnailLoaderTests
{
    // ============================================================
    // ReclaimExpiredThumbnails：核心清理逻辑（纯文件 I/O，无需 STA）
    // ============================================================

    [Fact]
    public void 回收_过期缓存删除_保留期内缓存保留()
    {
        string dir = CreateTempCacheDir();
        try
        {
            string oldFile = Path.Combine(dir, "aaaa.jpg");
            string newFile = Path.Combine(dir, "bbbb.jpg");
            File.WriteAllBytes(oldFile, new byte[] { 1, 2, 3 });
            File.WriteAllBytes(newFile, new byte[] { 4, 5, 6 });
            DateTime now = DateTime.UtcNow;
            File.SetLastWriteTimeUtc(oldFile, now.AddDays(-31)); // 超过 30 天保留期
            File.SetLastWriteTimeUtc(newFile, now.AddDays(-1));  // 保留期内

            int reclaimed = ThumbnailLoader.ReclaimExpiredThumbnails(now.AddDays(-30), dir);

            Assert.Equal(1, reclaimed);
            Assert.False(File.Exists(oldFile));
            Assert.True(File.Exists(newFile));
        }
        finally { DeleteTempDir(dir); }
    }

    [Fact]
    public void 回收_恰好等于cutoff边界_保留()
    {
        string dir = CreateTempCacheDir();
        try
        {
            string file = Path.Combine(dir, "cccc.jpg");
            File.WriteAllBytes(file, new byte[] { 1 });
            DateTime cutoff = DateTime.UtcNow;
            File.SetLastWriteTimeUtc(file, cutoff); // 恰好等于 cutoff：严格小于才清理

            int reclaimed = ThumbnailLoader.ReclaimExpiredThumbnails(cutoff, dir);

            Assert.Equal(0, reclaimed);
            Assert.True(File.Exists(file));
        }
        finally { DeleteTempDir(dir); }
    }

    [Fact]
    public void 回收_缓存目录不存在_返回0()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"CloudPanThumb_{Guid.NewGuid():N}_missing");

        int reclaimed = ThumbnailLoader.ReclaimExpiredThumbnails(DateTime.UtcNow, dir);

        Assert.Equal(0, reclaimed);
        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public void 回收_非jpg文件不受影响()
    {
        string dir = CreateTempCacheDir();
        try
        {
            // 缓存目录只有 *.jpg 是缩略图；.tmp 残留文件不参与清理
            string tmp = Path.Combine(dir, "abcdef.tmp");
            File.WriteAllBytes(tmp, new byte[] { 1 });
            File.SetLastWriteTimeUtc(tmp, DateTime.UtcNow.AddDays(-100));

            int reclaimed = ThumbnailLoader.ReclaimExpiredThumbnails(DateTime.UtcNow, dir);

            Assert.Equal(0, reclaimed);
            Assert.True(File.Exists(tmp));
        }
        finally { DeleteTempDir(dir); }
    }

    // ============================================================
    // ScheduleCacheReclaim：后台触发端到端（WinForms 控件需 STA）
    // ============================================================

    [Fact]
    public void 调度回收_后台线程删除过期缓存()
    {
        string dir = CreateTempCacheDir();
        string oldFile = Path.Combine(dir, "aaaa.jpg");
        File.WriteAllBytes(oldFile, new byte[] { 1, 2, 3 });
        File.SetLastWriteTimeUtc(oldFile, DateTime.UtcNow.AddDays(-31)); // 超过 30 天保留期

        RunOnSta(() =>
        {
            var list = new ListView();
            var loader = new ThumbnailLoader(list);
            try
            {
                loader.ScheduleCacheReclaim(dir).GetAwaiter().GetResult();
                Assert.False(File.Exists(oldFile));
            }
            finally
            {
                loader.Images.Dispose();
                list.Dispose();
            }
        });

        DeleteTempDir(dir);
    }

    // ============================================================
    // 辅助
    // ============================================================

    /// <summary>创建独立临时缓存目录（测试用例互不干扰，不触碰真实 %LocalAppData%）。</summary>
    private static string CreateTempCacheDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"CloudPanThumb_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void DeleteTempDir(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { }
    }

    /// <summary>在 STA 线程运行 WinForms 控件测试体（控件句柄需 STA）。</summary>
    private static void RunOnSta(Action body)
    {
        Exception? ex = null;
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (Exception e) { ex = e; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (ex != null)
        {
            ExceptionDispatchInfo.Capture(ex).Throw();
        }
    }
}
