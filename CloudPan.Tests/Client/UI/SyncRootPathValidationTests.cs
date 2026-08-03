using CloudPan.Client.UI;
using Xunit;

namespace CloudPan.Tests.Client.UI;

/// <summary>
/// 同步根路径安全校验单测（T-075）：SettingsForm 保存前复用 SetupForm.ValidateFolderSafety
/// 拒存磁盘根目录/系统目录/网络盘/可移动磁盘/.cloudpan 元数据目录。
/// </summary>
public class SyncRootPathValidationTests
{
    [Fact]
    public void ValidateFolderSafety_磁盘根目录_拒绝()
    {
        string root = Path.GetPathRoot(Environment.CurrentDirectory)!;
        Assert.NotNull(SetupForm.ValidateFolderSafety(root));
    }

    [Fact]
    public void ValidateFolderSafety_系统目录_拒绝()
    {
        string sysDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        Assert.NotNull(SetupForm.ValidateFolderSafety(sysDir));
        Assert.NotNull(SetupForm.ValidateFolderSafety(Path.Combine(sysDir, "System32")));
    }

    [Fact]
    public void ValidateFolderSafety_路径含cloudpan段_拒绝()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"CloudPanPathTest_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(tempDir, "sync", ".cloudpan"));
            Assert.NotNull(SetupForm.ValidateFolderSafety(Path.Combine(tempDir, "sync", ".cloudpan")));
            // 子目录同样拒绝
            Assert.NotNull(SetupForm.ValidateFolderSafety(Path.Combine(tempDir, "sync", ".cloudpan", "logs")));
            // 仅同名非精确段（my.cloudpan.backup）不受影响
            Directory.CreateDirectory(Path.Combine(tempDir, "sync", "my.cloudpan.backup"));
            Assert.Null(SetupForm.ValidateFolderSafety(Path.Combine(tempDir, "sync", "my.cloudpan.backup")));
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void ValidateFolderSafety_有效用户文件夹_通过()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"CloudPanPathTest_{Guid.NewGuid():N}");
        try
        {
            string syncRoot = Path.Combine(tempDir, "sync");
            Directory.CreateDirectory(syncRoot);
            Assert.Null(SetupForm.ValidateFolderSafety(syncRoot));
            // 客户端会自动创建不存在的同步根，因此不存在目录也应通过
            Assert.Null(SetupForm.ValidateFolderSafety(Path.Combine(tempDir, "sync-待创建")));
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void ValidateFolderSafety_路径含非法字符_拒绝()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"CloudPanPathTest_{Guid.NewGuid():N}");
        try
        {
            // \u0000 为非法路径字符，GetFullPath 抛异常 → 返回「路径无效」
            Assert.NotNull(SetupForm.ValidateFolderSafety(tempDir + "\u0000invalid"));
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }
}
