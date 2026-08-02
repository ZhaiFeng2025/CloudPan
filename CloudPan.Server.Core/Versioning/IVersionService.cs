namespace CloudPan.Server.Core;

/// <summary>
/// 全局版本号分配服务接口。
/// </summary>
public interface IVersionService
{
    Task<int> NextVersionAsync();
    Task<int> GetCurrentVersionAsync();
}
