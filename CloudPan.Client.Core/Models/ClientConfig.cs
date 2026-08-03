using System.Text.Json;

namespace CloudPan.Client.Core.Models;

/// <summary>
/// 客户端持久化配置（JSON 存储，支持版本迁移）。
/// 替代原来脆弱的 config.txt 三行文本格式。
/// </summary>
public class ClientConfig
{
    public int SchemaVersion { get; set; } = 1;
    public string ServerUrl { get; set; } = "";
    public string SyncRoot { get; set; } = "";
    public string TokenEncrypted { get; set; } = "";  // DPAPI 加密后的 Base64
    public long UploadLimitBps { get; set; } = 0;
    public long DownloadLimitBps { get; set; } = 0;
    public List<string> SelectedPaths { get; set; } = new() { "/" };

    /// <summary>用户是否已了解关闭窗口时隐藏到托盘的行为（原 ClientSettings.TrayCloseAcknowledged，T-043 并入）。</summary>
    public bool TrayCloseAcknowledged { get; set; }

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    /// <summary>加载配置。文件不存在或损坏时返回默认配置。</summary>
    public static ClientConfig Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                string json = File.ReadAllText(path);
                var config = JsonSerializer.Deserialize<ClientConfig>(json, JsonOpts);
                if (config != null && config.SchemaVersion >= 1)
                {
                    return config;
                }
            }
        }
        catch { /* 损坏的配置等同于不存在，从头开始 */ }

        // 尝试从旧版 config.txt 迁移
        string oldPath = Path.Combine(Path.GetDirectoryName(path)!, "config.txt");
        if (File.Exists(oldPath))
        {
            try
            {
                string[] lines = File.ReadAllLines(oldPath);
                if (lines.Length >= 2)
                {
                    ClientConfig migrated = new ClientConfig
                    {
                        ServerUrl = lines[0],
                        SyncRoot = lines[1],
                        TokenEncrypted = lines.Length >= 3 ? lines[2] : "",
                    };
                    migrated.Save(path);
                    File.Delete(oldPath); // 迁移后删除旧文件
                    return migrated;
                }
            }
            catch { }
        }

        return new ClientConfig();
    }

    /// <summary>保存配置到磁盘。</summary>
    public void Save(string path)
    {
        string? dir = Path.GetDirectoryName(path);
        if (dir != null)
        {
            Directory.CreateDirectory(dir);
        }

        // 原子写入：先写 .tmp，再 rename
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOpts));
        File.Move(tmp, path, overwrite: true);
    }
}
