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
/// </summary>
public static class SettingsStore
{
    private static readonly string FilePath;

    static SettingsStore()
    {
        string configDir = Path.Combine(Program.SyncRoot, ".cloudpan");
        FilePath = Path.Combine(configDir, "settings.json");
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
