using Microsoft.AspNetCore.Builder;

namespace CloudPan.Server.Hosting;

/// <summary>
/// 可选 UI 桥接口（T-015 / R-Q3）：Host 编译期不引用 Server.UI，运行期经 <see cref="UIBridgeLocator"/> 反射发现实现。
/// headless（Server.UI 程序集未部署）时桥为 null，Host 以纯 Kestrel 服务运行；带 UI 时经本接口接入托盘 GUI。
/// 实现位于 Server.UI（ServerUiBridge），两者依赖方向：Host 不依赖 UI，UI 依赖 Host 提供的桥契约。
/// </summary>
public interface IHostUIBridge
{
    /// <summary>以托盘 GUI 模式运行（实现内部完成 WinForms 初始化、窗口/托盘创建与消息循环）。</summary>
    Task RunTrayAsync(WebApplication app, string[] args);
}
