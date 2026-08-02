namespace CloudPan.Server.Core;

/// <summary>
/// 运行时设置服务——AppConfig 键值表的类型化门面。
/// 仅处理 persistence=appconfig 的设置（Token 轮换走 ITokenService，不在此读写）。
/// 启动期参数（端口/同步根目录）走 ServerSettingsFile，不在此列。
/// </summary>
public interface ISettingsService
{
    /// <summary>读取原始字符串值；键不存在返回 null。</summary>
    Task<string?> GetAsync(string key);

    /// <summary>读取字符串，缺省回退 defaultValue。</summary>
    Task<string> GetStringAsync(string key, string defaultValue);

    /// <summary>读取整数，缺省或解析失败回退 defaultValue。</summary>
    Task<int> GetIntAsync(string key, int defaultValue);

    /// <summary>写入字符串（INSERT OR IGNORE + UPDATE，原子）。</summary>
    Task SetStringAsync(string key, string value);

    /// <summary>写入整数。</summary>
    Task SetIntAsync(string key, int value);
}
