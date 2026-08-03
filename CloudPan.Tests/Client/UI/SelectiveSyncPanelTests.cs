using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows.Forms;
using CloudPan.Client.Core.Models;
using CloudPan.Client.Core.Services;
using CloudPan.Client.UI;
using CloudPan.Contract;
using CloudPan.Infrastructure.Models;
using CloudPan.Infrastructure.Persistence.Client;
using CloudPan.Tests.Client.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CloudPan.Tests.Client.UI;

/// <summary>
/// 选择性同步面板（SelectiveSyncPanel）单测（T-074）：
/// 空树不覆盖既有排除配置 + 填充目录树后 SelectedPaths 与勾选态一致。
/// WinForms 控件需在 STA 线程实例化，故测试体经 RunOnSta 承载。
/// </summary>
public class SelectiveSyncPanelTests
{
    // ============================================================
    // AC2：空树 + 已有排除配置时 getter 返回配置而非 { "/" }
    // ============================================================

    [Fact]
    public void SelectedPaths_树未加载且已有排除配置_返回既有配置而非全选()
    {
        RunOnSta(() =>
        {
            var panel = new SelectiveSyncPanel();
            try
            {
                // 用户既有排除集（设置页打开时经 setter 注入），目录树尚未加载
                panel.SelectedPaths = new List<string> { "/photos/" };

                var result = panel.SelectedPaths;

                // 不得静默回退 { "/" } 全选覆盖用户排除集
                Assert.Equal(new List<string> { "/photos/" }, result);
                Assert.False(panel.IsTreeLoaded);
            }
            finally { panel.Dispose(); }
        });
    }

    [Fact]
    public void SelectedPaths_树未加载且无既有配置_返回默认全选()
    {
        RunOnSta(() =>
        {
            var panel = new SelectiveSyncPanel();
            try
            {
                // 从未 set 过配置：默认全选（与旧版行为一致）
                Assert.Equal(new List<string> { "/" }, panel.SelectedPaths);
            }
            finally { panel.Dispose(); }
        });
    }

    [Fact]
    public void SelectedPaths_树加载失败_返回既有配置而非全选()
    {
        RunOnSta(() =>
        {
            var panel = new SelectiveSyncPanel();
            try
            {
                panel.SelectedPaths = new List<string> { "/docs/private/" };
                panel.SetLoadFailed("服务端暂无目录列表。\n保存将不会修改当前的排除设置。");

                var result = panel.SelectedPaths;

                Assert.Equal(new List<string> { "/docs/private/" }, result);
                Assert.False(panel.IsTreeLoaded);
                Assert.NotNull(panel.TreeLoadMessage);
            }
            finally { panel.Dispose(); }
        });
    }

    // ============================================================
    // AC3：面板填充目录树后 SelectedPaths 与勾选态一致
    // ============================================================

    [Fact]
    public void SelectedPaths_填充目录树_默认全选_getter返回全选()
    {
        RunOnSta(() =>
        {
            var panel = new SelectiveSyncPanel();
            try
            {
                panel.LoadFromPaths(new[] { "/photos/", "/docs/private/", "/docs/public/" });

                Assert.True(panel.IsTreeLoaded);
                Assert.Equal(new List<string> { "/" }, panel.SelectedPaths);
            }
            finally { panel.Dispose(); }
        });
    }

    [Fact]
    public void SelectedPaths_填充目录树_取消勾选子树_排除集与勾选态一致()
    {
        RunOnSta(() =>
        {
            var panel = new SelectiveSyncPanel();
            try
            {
                panel.LoadFromPaths(new[] { "/photos/", "/docs/private/", "/docs/public/" });
                var tree = GetTree(panel);

                // 取消勾选 /photos（叶子）与 /docs（含子节点）——排除集应反映这两个子树
                FindNode(tree.Nodes, "/photos/")!.Checked = false;
                FindNode(tree.Nodes, "/docs/")!.Checked = false;

                var result = panel.SelectedPaths;

                Assert.Equal(2, result.Count);
                Assert.Contains("/photos/", result);
                Assert.Contains("/docs/", result);
                Assert.DoesNotContain("/", result);
            }
            finally { panel.Dispose(); }
        });
    }

    [Fact]
    public void SelectedPaths_填充目录树_既有排除配置回填_勾选态与配置一致()
    {
        RunOnSta(() =>
        {
            var panel = new SelectiveSyncPanel();
            try
            {
                // 先注入既有排除集，再填充目录树：/photos 应回填为未勾选
                panel.SelectedPaths = new List<string> { "/photos/" };
                panel.LoadFromPaths(new[] { "/photos/", "/docs/" });

                var tree = GetTree(panel);
                Assert.False(FindNode(tree.Nodes, "/photos/")!.Checked);
                Assert.True(FindNode(tree.Nodes, "/docs/")!.Checked);
                Assert.Equal(new List<string> { "/photos/" }, panel.SelectedPaths);
            }
            finally { panel.Dispose(); }
        });
    }

    // ============================================================
    // 目录树来源：SyncBrowseService.GetDirectoryTreePathsAsync（T-074）
    // ============================================================

    [Fact]
    public async Task GetDirectoryTreePaths_从快照聚合目录路径_规范化并去重()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"CloudPanSelPanel_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            string syncRoot = Path.Combine(tempDir, "sync");
            Directory.CreateDirectory(syncRoot);
            string dbPath = Path.Combine(tempDir, "client-test.db");
            var dbFactory = new TestClientDbFactory(dbPath);
            using (var db = dbFactory.CreateDbContext())
            {
                db.Database.EnsureCreated();
            }

            using (var db = dbFactory.CreateDbContext())
            {
                db.RemoteSnapshots.AddRange(
                    new RemoteSnapshot { Path = "/photos", Type = (int)FileType.Directory, State = (int)FileState.Synced },
                    new RemoteSnapshot { Path = "/photos/summer.jpg", Type = (int)FileType.File, State = (int)FileState.Synced },
                    new RemoteSnapshot { Path = "/docs", Type = (int)FileType.Directory, State = (int)FileState.Synced },
                    new RemoteSnapshot { Path = "/docs/", Type = (int)FileType.Directory, State = (int)FileState.Synced }); // 重复/带尾斜杠 → 去重
                await db.SaveChangesAsync();
            }

            var engine = new SyncEngine(
                new MockApiClient(),
                new SyncConfig { SyncRoot = syncRoot, ServerUrl = "http://localhost:8443" },
                new ClientStoreFactory(dbFactory),
                NullLoggerFactory.Instance.CreateLogger<SyncEngine>());

            var dirs = await engine.GetDirectoryTreePathsAsync();

            // 只返回目录、统一 / 开头 + / 结尾、去重、按路径排序
            Assert.Equal(new List<string> { "/docs/", "/photos/" }, dirs);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    // ============================================================
    // 辅助
    // ============================================================

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

    /// <summary>反射获取面板内部目录树。</summary>
    private static TreeView GetTree(SelectiveSyncPanel panel)
    {
        var field = typeof(SelectiveSyncPanel).GetField("_tree", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        return (TreeView)field!.GetValue(panel)!;
    }

    /// <summary>递归查找 Tag 匹配的节点。</summary>
    private static TreeNode? FindNode(TreeNodeCollection nodes, string tag)
    {
        foreach (TreeNode node in nodes)
        {
            if ((string?)node.Tag == tag)
            {
                return node;
            }
            var child = FindNode(node.Nodes, tag);
            if (child != null)
            {
                return child;
            }
        }
        return null;
    }
}
