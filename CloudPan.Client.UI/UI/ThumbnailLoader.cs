using System.Security.Cryptography;
using System.Text;
using CloudPan.Client.Core.Services;
using CloudPan.Contract;

namespace CloudPan.Client.UI;

/// <summary>
/// 网格视图缩略图加载器（T-087）——复用 /api/thumbnails，异步加载 + 本地小尺寸缓存 + 失败回退字形。
/// 自持缩略图 ImageList（索引 0=文件夹字形、1=文件字形、其余为缩略图），仅网格视图（View.LargeIcon）使用；
/// 列表视图保持字体图标不变。所有成员仅在 UI 线程访问（单线程消息泵，无并发竞争）。
/// </summary>
internal sealed class ThumbnailLoader
{
    /// <summary>服务端缩略图生成宽度（显示 40×40，请求稍大保证缩放后清晰；本地缓存 key 含宽度）。</summary>
    private const int ThumbWidth = 96;

    /// <summary>缩略图 ImageList 上限：超限重建（清内存索引，缩略图从本地缓存重新加载），防止长会话内存无界增长。</summary>
    private const int MaxThumbImages = 800;

    private readonly ListView _list;
    private readonly ImageList _images = new ImageList { ColorDepth = ColorDepth.Depth32Bit, ImageSize = new Size(40, 40) };
    private readonly Dictionary<string, int> _indexByPath = new(StringComparer.OrdinalIgnoreCase);
    private int _generation;
    private string? _cacheDir;

    /// <summary>缓存回收是否已调度（T-104）：首次网格渲染调度一次，防止会话内重复回收。</summary>
    private bool _cacheReclaimStarted;

    /// <summary>缩略图获取器（宿主注入，指向 ApiClient.GetThumbnailAsync）：参数（path, width, ct）→ JPEG 字节，失败返回 null。</summary>
    public Func<string, int, CancellationToken, Task<byte[]?>>? Fetcher { get; set; }

    public ThumbnailLoader(ListView list)
    {
        _list = list;
        _images.Images.Add(FileBrowseRender.DrawFolderGlyph()); // 0 文件夹
        _images.Images.Add(FileBrowseRender.DrawFileGlyph());   // 1 文件
        list.LargeImageList = _images;
    }

    /// <summary>网格 ImageList（含文件夹/文件字形与已加载缩略图）。</summary>
    public ImageList Images => _images;

    /// <summary>网格渲染入口：作废在途加载结果；网格视图为图片项应用已加载缩略图或异步加载，列表视图不处理。</summary>
    public void RenderGrid(IReadOnlyList<ListViewItem> items, bool grid)
    {
        _generation++; // 作废在途的旧加载结果，防止覆盖已换代列表（CLAUDE.md 7.2 异步生命周期）
        if (!grid)
        {
            return;
        }

        ScheduleCacheReclaimOnce(); // T-104：首次网格渲染时后台回收过期本地缓存，不阻塞 UI 线程
        EnsureCapacity();
        _list.LargeImageList = _images; // 重建后确保绑定（幂等）
        foreach (ListViewItem lvi in items)
        {
            if (lvi.Tag is FileBrowseItem item && FileBrowseRender.IsThumbnailImage(item))
            {
                Apply(item, lvi);
            }
        }
    }

    /// <summary>已加载缩略图直接复用索引；未加载保持文件字形（ImageIndex=1）并异步加载。</summary>
    private void Apply(FileBrowseItem item, ListViewItem lvi)
    {
        if (_indexByPath.TryGetValue(item.Path, out int index))
        {
            lvi.ImageIndex = index;
            return;
        }

        LoadThumbnailAsync(item, lvi, _generation);
    }

    /// <summary>超限重建：清空 ImageList（仅保留字形）与内存索引，缩略图从本地缓存重新加载。</summary>
    private void EnsureCapacity()
    {
        if (_images.Images.Count < MaxThumbImages)
        {
            return;
        }

        _images.Images.Clear();
        _images.Images.Add(FileBrowseRender.DrawFolderGlyph());
        _images.Images.Add(FileBrowseRender.DrawFileGlyph());
        _indexByPath.Clear();
    }

    /// <summary>异步加载缩略图：本地缓存 → 网络 /api/thumbnails，成功后替换该列表项图标。失败静默回退字形，不阻塞列表。</summary>
    private async void LoadThumbnailAsync(FileBrowseItem item, ListViewItem lvi, int generation)
    {
        try
        {
            byte[]? bytes = await ReadThumbnailBytesAsync(item);
            if (bytes == null)
            {
                return; // 获取失败：保持文件字形（回退）
            }

            // 续段回到 UI 线程（await 前捕获 WindowsForms SynchronizationContext）
            if (generation != _generation)
            {
                return; // 渲染已换代，当前帧丢弃（缩略图已写本地缓存，下帧命中）
            }

            if (_indexByPath.TryGetValue(item.Path, out int existing))
            {
                lvi.ImageIndex = existing; // 并发的同路径加载已就绪，直接复用
                return;
            }

            using var ms = new MemoryStream(bytes);
            using var thumb = new Bitmap(ms);
            int index = _images.Images.Count;
            _images.Images.Add(thumb);
            _indexByPath[item.Path] = index;
            lvi.ImageIndex = index;
            _list.Invalidate();
        }
        catch (Exception ex)
        {
            // 顶层兜底：解码/线程异常一律回退字形，不冒泡到 UI 线程（CLAUDE.md 7.2）
            System.Diagnostics.Debug.WriteLine($"缩略图加载失败: {item.Path}: {ex.Message}");
        }
    }

    /// <summary>读取缩略图字节：优先本地缓存，未命中经 Fetcher 拉取 /api/thumbnails 并写入本地缓存（原子写）。</summary>
    private async Task<byte[]?> ReadThumbnailBytesAsync(FileBrowseItem item)
    {
        string cachePath = GetThumbCachePath(item);
        if (File.Exists(cachePath))
        {
            try
            {
                return await File.ReadAllBytesAsync(cachePath);
            }
            catch (Exception ex)
            {
                // 缓存读失败（损坏/占用）：转网络获取，不阻塞
                System.Diagnostics.Debug.WriteLine($"缩略图缓存读取失败，转网络: {cachePath}: {ex.Message}");
            }
        }

        if (Fetcher == null)
        {
            return null;
        }

        byte[]? bytes = await Fetcher(item.Path, ThumbWidth, CancellationToken.None);
        if (bytes == null || bytes.Length == 0)
        {
            return null;
        }

        try
        {
            string? dir = Path.GetDirectoryName(cachePath);
            if (dir != null)
            {
                Directory.CreateDirectory(dir);
            }

            // 原子写缓存：先写 .tmp 再 rename（对齐项目原子写约定）
            string tmpPath = cachePath + ".tmp";
            await File.WriteAllBytesAsync(tmpPath, bytes);
            File.Move(tmpPath, cachePath, overwrite: true);
        }
        catch (Exception ex)
        {
            // 缓存写失败不影响本次显示
            System.Diagnostics.Debug.WriteLine($"缩略图缓存写入失败: {cachePath}: {ex.Message}");
        }

        return bytes;
    }

    /// <summary>计算缩略图本地缓存路径。key = SHA-256(路径|宽度|服务端文件版本) 前 16 hex；文件更新（版本提升）后 key 变化自动失效重取。</summary>
    private string GetThumbCachePath(FileBrowseItem item)
    {
        string cacheDir = GetCacheDir();

        string key = $"{item.Path}|w={ThumbWidth}|v={item.Version}";
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16];
        return Path.Combine(cacheDir, hash + ".jpg");
    }

    /// <summary>本地缩略图缓存目录（%LocalAppData%\CloudPan\thumbnails），惰性解析一次。</summary>
    private string GetCacheDir() => _cacheDir ??= Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CloudPan", "thumbnails");

    /// <summary>
    /// 首次网格渲染时调度一次本地缓存回收（T-104）：防止会话内重复回收。
    /// 回收在后台线程执行（Task.Run），不阻塞 UI 线程渲染。
    /// </summary>
    private void ScheduleCacheReclaimOnce()
    {
        if (_cacheReclaimStarted)
        {
            return;
        }
        _cacheReclaimStarted = true;
        _ = ScheduleCacheReclaim();
    }

    /// <summary>
    /// 后台回收过期本地缩略图缓存（T-104）：删除超过保留期（对齐服务端 T-088，读
    /// SpecConfig.ThumbnailCacheRetentionDays=30 天）的缓存文件。缓存 key 含版本，文件更新后旧缓存
    /// 孤儿化即失效，由本回收清除——不再只增不减。
    /// 缓存目录在调用线程捕获，后台任务仅做文件删除，不访问 UI 成员（CLAUDE.md 7.4）；
    /// Task.Run 包裹完整 try-catch，异常不回冒不丢失（CLAUDE.md 7.2）。返回回收任务，调用方可忽略或等待。
    /// </summary>
    internal Task ScheduleCacheReclaim(string? cacheDir = null)
    {
        string dir = cacheDir ?? GetCacheDir();
        DateTime cutoff = DateTime.UtcNow.AddDays(-SpecConfig.ThumbnailCacheRetentionDays);
        return Task.Run(() =>
        {
            try
            {
                ReclaimExpiredThumbnails(cutoff, dir);
            }
            catch (Exception ex)
            {
                // 兜底：回收失败不影响网格渲染，缓存为派生数据，下次启动再试
                System.Diagnostics.Debug.WriteLine($"缩略图缓存回收失败: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// 清理过期缩略图缓存：删除最后写入早于 cutoff 的 *.jpg（缓存写入后不再改动，LastWriteTime 即
    /// 创建/最后使用时间，LRU 语义）。派生数据重建成本低，按保留期清理即可控制长期磁盘占用。
    /// 尽力而为：单文件/单目录失败不影响其余清理。返回清理文件数。
    /// </summary>
    internal static int ReclaimExpiredThumbnails(DateTime cutoff, string cacheDir)
    {
        if (!Directory.Exists(cacheDir))
        {
            return 0;
        }

        int reclaimed = 0;
        try
        {
            foreach (string cacheFile in Directory.EnumerateFiles(cacheDir, "*.jpg"))
            {
                try
                {
                    // 缓存文件写入后不再改动：LastWriteTime 即创建/最后使用时间（LRU 语义）
                    if (new FileInfo(cacheFile).LastWriteTimeUtc < cutoff)
                    {
                        File.Delete(cacheFile);
                        reclaimed++;
                    }
                }
                catch
                {
                    // 竞态：文件可能刚被写入/占用（重建成本低，下轮/下次回收再试）
                }
            }
        }
        catch
        {
            // 目录枚举失败（权限/竞态）：尽力而为，返回已回收数
        }
        return reclaimed;
    }
}
