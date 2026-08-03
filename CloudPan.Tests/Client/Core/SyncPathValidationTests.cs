using CloudPan.Client.Core.Services;
using Xunit;

namespace CloudPan.Tests.Client.Core;

/// <summary>
/// 客户端路径安全统一防线单测（T-085）：SyncPath.ToLocalPath 对越界相对路径拒绝落盘、正常子路径通过。
/// 覆盖 '../' 路径穿越（同步根外写出）与正常子路径两条核心验收路径。
/// </summary>
public class SyncPathValidationTests
{
    private static string CreateSyncRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), $"CloudPanSyncRoot_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static void Cleanup(string root)
    {
        try { Directory.Delete(root, recursive: true); } catch { }
    }

    [Fact]
    public void ToLocalPath_越界上级跳转_拒绝()
    {
        string root = CreateSyncRoot();
        try
        {
            // ../ 越界（路径穿越）：写入同步根之外必须被拒绝
            Assert.Throws<ArgumentException>(() => SyncPath.ToLocalPath(root, "../evil.txt"));
            Assert.Throws<ArgumentException>(() => SyncPath.ToLocalPath(root, "/../../evil.txt"));
            // 深层 ../ 同样越界
            Assert.Throws<ArgumentException>(() => SyncPath.ToLocalPath(root, "a/b/../../../evil.txt"));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void ToLocalPath_绝对路径_拒绝()
    {
        string root = CreateSyncRoot();
        try
        {
            // Windows Path.Combine 遇绝对路径会整体替换 syncRoot → 必须拒绝
            string outside = Path.Combine(Path.GetTempPath(), $"CloudPanOutside_{Guid.NewGuid():N}");
            Assert.Throws<ArgumentException>(() => SyncPath.ToLocalPath(root, outside));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void ToLocalPath_正常子路径_通过()
    {
        string root = CreateSyncRoot();
        try
        {
            string local = SyncPath.ToLocalPath(root, "photos/vacation/IMG_001.jpg");
            string expected = @"\\?\" + Path.Combine(Path.GetFullPath(root), "photos", "vacation", "IMG_001.jpg");
            Assert.Equal(expected, local);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void ToLocalPath_前导斜杠正常路径_通过()
    {
        string root = CreateSyncRoot();
        try
        {
            string local = SyncPath.ToLocalPath(root, "/docs/report.docx");
            string expected = @"\\?\" + Path.Combine(Path.GetFullPath(root), "docs", "report.docx");
            Assert.Equal(expected, local);
        }
        finally { Cleanup(root); }
    }
}
