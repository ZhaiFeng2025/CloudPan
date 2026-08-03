using System.ComponentModel;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows.Forms;
using CloudPan.Client.Core.Services;
using CloudPan.Client.UI;
using CloudPan.Contract;
using Xunit;

namespace CloudPan.Tests.Client.UI;

/// <summary>
/// 文件浏览视图多选/批量操作/右键菜单单测（T-083）：
/// ListView 开启 MultiSelect、多选批量删除事件载荷、批量下载仅含 CloudOnly 子集、右键菜单结构与可用性。
/// WinForms 控件需在 STA 线程实例化，故测试体经 RunOnSta 承载。
/// </summary>
public class FileBrowserViewTests
{
    // ============================================================
    // AC1：ListView 开启 MultiSelect
    // ============================================================

    [Fact]
    public void MultiSelect_列表视图已开启多选()
    {
        RunOnSta(() =>
        {
            var view = new FileBrowserView();
            try
            {
                Assert.True(GetField<ListView>(view, "_list").MultiSelect);
            }
            finally { view.Dispose(); }
        });
    }

    // ============================================================
    // 多选批量删除：按钮文本 + 事件载荷（AC2 结构部分）
    // ============================================================

    [Fact]
    public void 多选多个文件_删除按钮文本为批量删除_事件载荷为全部选中项()
    {
        RunOnSta(() =>
        {
            var view = new FileBrowserView();
            try
            {
                var list = GetField<ListView>(view, "_list");
                FillAndSelect(view, list, new[]
                {
                    Lvi("/a.jpg", false, FileState.CloudOnly, false),
                    Lvi("/b.txt", false, FileState.Synced, true),
                    Lvi("/photos", true, FileState.Synced, true),
                });

                var deleteBtn = GetField<Button>(view, "_deleteButton");
                Assert.True(deleteBtn.Enabled);
                Assert.Equal("批量删除", deleteBtn.Text);

                var captured = new List<FileBrowseItem>();
                void OnDelete(IReadOnlyList<FileBrowseItem> items) => captured.AddRange(items);
                view.DeleteRequested += OnDelete;
                deleteBtn.PerformClick();

                Assert.Equal(3, captured.Count);
                Assert.Contains(captured, i => i.Path == "/a.jpg");
                Assert.Contains(captured, i => i.Path == "/b.txt");
                Assert.Contains(captured, i => i.Path == "/photos");
            }
            finally { view.Dispose(); }
        });
    }

    [Fact]
    public void 单选一个文件_删除按钮文本为删除_分享版本可用()
    {
        RunOnSta(() =>
        {
            var view = new FileBrowserView();
            try
            {
                var list = GetField<ListView>(view, "_list");
                FillAndSelect(view, list, new[] { Lvi("/a.jpg", false, FileState.Synced, true) });

                var deleteBtn = GetField<Button>(view, "_deleteButton");
                Assert.Equal("删除", deleteBtn.Text);
                Assert.True(GetField<Button>(view, "_shareButton").Enabled);
                Assert.True(GetField<Button>(view, "_versionButton").Enabled);
            }
            finally { view.Dispose(); }
        });
    }

    [Fact]
    public void 多选多个文件_分享版本按钮禁用()
    {
        RunOnSta(() =>
        {
            var view = new FileBrowserView();
            try
            {
                var list = GetField<ListView>(view, "_list");
                FillAndSelect(view, list, new[]
                {
                    Lvi("/a.jpg", false, FileState.Synced, true),
                    Lvi("/b.txt", false, FileState.Synced, true),
                });

                Assert.False(GetField<Button>(view, "_shareButton").Enabled);
                Assert.False(GetField<Button>(view, "_versionButton").Enabled);
            }
            finally { view.Dispose(); }
        });
    }

    // ============================================================
    // 批量下载：事件仅含 CloudOnly 且本地不存在的选中文件
    // ============================================================

    [Fact]
    public void 多选批量下载_事件仅含CloudOnly可下载项()
    {
        RunOnSta(() =>
        {
            var view = new FileBrowserView();
            try
            {
                var list = GetField<ListView>(view, "_list");
                FillAndSelect(view, list, new[]
                {
                    Lvi("/a.jpg", false, FileState.CloudOnly, false), // 可下载
                    Lvi("/b.txt", false, FileState.Synced, true),     // 本地已有，不可下载
                    Lvi("/photos", true, FileState.Synced, true),     // 目录，不可下载
                });

                var downloadBtn = GetField<Button>(view, "_downloadButton");
                Assert.True(downloadBtn.Enabled);

                var captured = new List<FileBrowseItem>();
                void OnDownload(IReadOnlyList<FileBrowseItem> items) => captured.AddRange(items);
                view.DownloadRequested += OnDownload;
                downloadBtn.PerformClick();

                var path = Assert.Single(captured).Path;
                Assert.Equal("/a.jpg", path);
            }
            finally { view.Dispose(); }
        });
    }

    // ============================================================
    // 右键上下文菜单（AC3 结构部分）：条目 + 弹出前可用性
    // ============================================================

    [Fact]
    public void 右键菜单_含打开下载分享版本历史删除入口()
    {
        RunOnSta(() =>
        {
            var view = new FileBrowserView();
            try
            {
                var menu = GetField<ContextMenuStrip>(view, "_listMenu");
                List<string> texts = menu.Items
                    .OfType<ToolStripItem>()
                    .Select(i => i.Text ?? "")
                    .Where(t => t.Length > 0)
                    .ToList();

                Assert.Contains("打开", texts);
                Assert.Contains("下载到本机", texts);
                Assert.Contains("分享", texts);
                Assert.Contains("版本历史", texts);
                Assert.Contains("删除", texts);
            }
            finally { view.Dispose(); }
        });
    }

    [Fact]
    public void 右键菜单弹出前_多选文件时删除可用分享版本打开禁用()
    {
        RunOnSta(() =>
        {
            var view = new FileBrowserView();
            try
            {
                var list = GetField<ListView>(view, "_list");
                FillAndSelect(view, list, new[]
                {
                    Lvi("/a.jpg", false, FileState.CloudOnly, false),
                    Lvi("/b.txt", false, FileState.Synced, true),
                });

                var menu = GetField<ContextMenuStrip>(view, "_listMenu");
                InvokeOpening(view, menu);

                Assert.True(GetField<ToolStripMenuItem>(view, "_menuDeleteItem").Enabled);
                Assert.True(GetField<ToolStripMenuItem>(view, "_menuDownloadItem").Enabled); // a.jpg 为 CloudOnly 可下载
                Assert.False(GetField<ToolStripMenuItem>(view, "_menuShareItem").Enabled);
                Assert.False(GetField<ToolStripMenuItem>(view, "_menuVersionItem").Enabled);
                Assert.False(GetField<ToolStripMenuItem>(view, "_menuOpenItem").Enabled);
            }
            finally { view.Dispose(); }
        });
    }

    [Fact]
    public void 右键菜单弹出前_单选文件时打开分享版本删除均可用()
    {
        RunOnSta(() =>
        {
            var view = new FileBrowserView();
            try
            {
                var list = GetField<ListView>(view, "_list");
                FillAndSelect(view, list, new[] { Lvi("/a.jpg", false, FileState.Synced, true) });

                var menu = GetField<ContextMenuStrip>(view, "_listMenu");
                InvokeOpening(view, menu);

                Assert.True(GetField<ToolStripMenuItem>(view, "_menuDeleteItem").Enabled);
                Assert.True(GetField<ToolStripMenuItem>(view, "_menuShareItem").Enabled);
                Assert.True(GetField<ToolStripMenuItem>(view, "_menuVersionItem").Enabled);
                Assert.True(GetField<ToolStripMenuItem>(view, "_menuOpenItem").Enabled);
                Assert.False(GetField<ToolStripMenuItem>(view, "_menuDownloadItem").Enabled); // Synced 本地已有，无下载项
            }
            finally { view.Dispose(); }
        });
    }

    // ============================================================
    // 辅助
    // ============================================================

    /// <summary>构造一个带 Tag 的列表项。</summary>
    private static ListViewItem Lvi(string path, bool isDir, FileState state, bool localExists)
    {
        string name = path[(path.LastIndexOf('/') + 1)..];
        return new ListViewItem(name)
        {
            Tag = new FileBrowseItem(path, name, isDir, 100, 1, (int)state, localExists),
        };
    }

    /// <summary>向列表填充项并全选（触发 SelectedIndexChanged → UpdateSelection）。</summary>
    private static void FillAndSelect(FileBrowserView view, ListView list, IEnumerable<ListViewItem> items)
    {
        list.CreateControl(); // 确保句柄可用以设置选中状态
        list.BeginUpdate();
        try
        {
            list.Items.Clear();
            foreach (ListViewItem lvi in items)
            {
                list.Items.Add(lvi);
            }
        }
        finally
        {
            list.EndUpdate();
        }

        foreach (ListViewItem lvi in list.Items)
        {
            lvi.Selected = true;
        }
    }

    /// <summary>反射调用右键菜单 Opening 逻辑（刷新可用性）。</summary>
    private static void InvokeOpening(FileBrowserView view, ContextMenuStrip menu)
    {
        var method = typeof(FileBrowserView).GetMethod("ListMenu_Opening", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        method!.Invoke(view, new object[] { menu, new CancelEventArgs() });
    }

    /// <summary>反射读取私有字段。</summary>
    private static T GetField<T>(object target, string name)
    {
        var field = typeof(FileBrowserView).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        return (T)field!.GetValue(target)!;
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
