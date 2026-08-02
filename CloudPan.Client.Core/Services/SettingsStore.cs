using System.Text.Json;

namespace CloudPan.Client.Services;

/// <summary>
/// 客户端设置——JSON 持久化，保存到 .cloudpan/settings.json。
/// </summary>
public class ClientSettings
{
    /// <summary>用户是否已了解关闭窗口时隐藏到托盘的行为。</summary>
    public bool TrayCloseAcknowledged { get; set; }

    /// <summary>持久化到磁盘。</summary>
    public void Save() => SettingsStore.Save(this);
}

/// <summary>
/// 设置存储管理——加载/保存 ClientSettings。
/// syncRoot 由 UI 在配置完成后显式注入（SetSyncRoot），避免静态捕获陈旧路径。
/// </summary>
public static class SettingsStore
{
    private static string _syncRoot = "";

    private static string FilePath => Path.Combine(_syncRoot, ".cloudpan", "settings.json");

    /// <summary>初始化同步根目录（程序配置完成后调用，见 Client.UI Program.cs）。</summary>
    public static void SetSyncRoot(string syncRoot)
    {
        _syncRoot = syncRoot ?? "";
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    /// <summary>从磁盘加载设置，不存在或读取失败时返回默认值。</summary>
    public static ClientSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                string json = File.ReadAllText(FilePath);
                return JsonSerializer.Deserialize<ClientSettings>(json) ?? new ClientSettings();
            }
        }
        catch
        {
            // 读取失败返回默认值，不影响主流程
        }
        return new ClientSettings();
    }

    /// <summary>将设置持久化到磁盘。</summary>
    public static void Save(ClientSettings settings)
    {
        try
        {
            string? dir = Path.GetDirectoryName(FilePath);
            if (dir != null)
            {
                Directory.CreateDirectory(dir);
            }

            string json = JsonSerializer.Serialize(settings, JsonOptions);
            File.WriteAllText(FilePath, json);
        }
        catch
        {
            // 写失败不应影响主流程
        }
    }
}
