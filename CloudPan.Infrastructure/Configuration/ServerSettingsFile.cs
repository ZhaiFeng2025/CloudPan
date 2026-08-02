using System.Text.Json;

namespace CloudPan.Infrastructure.Configuration;

/// <summary>
/// 服务端启动期设置（端口/同步根目录）。与运行时设置（AppConfig 表）不同：
/// 启动期参数必须在 Kestrel 绑定/DB 打开前决定，故存独立 JSON 文件而非 DB。
/// </summary>
public sealed record BootstrapSettings(int? Port, string? SyncRoot);

/// <summary>
/// server-settings.json 存取。位于 exe 目录，服务(LocalSystem)与托盘(用户)同源读取。
/// 读取失败（缺失/损坏）容错返回 null——调用方回退默认值，绝不阻断启动。
/// </summary>
public static class ServerSettingsFile
{
    private const string FileName = "server-settings.json";

    /// <summary>
    /// 定位设置文件路径。
    /// 常规运行：AppContext.BaseDirectory 即 bin 目录（含 CloudPan.Server.exe）。
    /// 单文件自解压发布：BaseDirectory 指向解压临时目录，需退回 Environment.ProcessPath 所在目录。
    /// </summary>
    public static string GetSettingsPath()
    {
        string baseDir = AppContext.BaseDirectory;
        if (File.Exists(Path.Combine(baseDir, "CloudPan.Server.exe")))
        {
            return Path.Combine(baseDir, FileName);
        }

        string? processDir = Path.GetDirectoryName(Environment.ProcessPath);
        return Path.Combine(string.IsNullOrEmpty(processDir) ? baseDir : processDir, FileName);
    }

    /// <summary>读取设置；文件缺失或 JSON 损坏返回 null（调用方回退默认值）。</summary>
    public static BootstrapSettings? Load()
    {
        string path = GetSettingsPath();
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            string json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<BootstrapSettings>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch (Exception)
        {
            // 配置损坏 → 回退默认值（与缺失同语义）。启动不应被损坏的设置文件阻断。
            return null;
        }
    }

    /// <summary>原子写入：先写 .tmp 再 rename，避免中途崩溃留下半成品覆盖原文件。</summary>
    public static void Save(BootstrapSettings settings)
    {
        string path = GetSettingsPath();
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        string tmpPath = path + ".tmp";
        File.WriteAllText(tmpPath, json);
        File.Move(tmpPath, path, overwrite: true);
    }
}
