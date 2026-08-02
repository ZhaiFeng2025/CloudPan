using System.Reflection;
using System.Runtime.Loader;
using Serilog;

namespace CloudPan.Server.Host.Hosting;

/// <summary>
/// 可选 UI 桥定位器（T-015 / R-Q3）：发现并实例化 IHostUIBridge 实现。
/// Server.UI 程序集随发布输出存在时加载并返回桥；headless 部署（无 Server.UI.dll）返回 null，Host 以纯 Kestrel 服务运行。
/// </summary>
public static class UIBridgeLocator
{
    /// <summary>
    /// 从 Host 输出目录按路径加载 CloudPan.Server.UI.dll（Host 已不编译期引用，故不能经 ProjectReference 传递，
    /// 需运行时探测）。返回 null 表示 UI 未部署或加载失败——调用方回退 headless 模式。
    /// </summary>
    public static IHostUIBridge? Find()
    {
        string uiPath = Path.Combine(AppContext.BaseDirectory, "CloudPan.Server.UI.dll");
        if (!File.Exists(uiPath))
        {
            return null; // UI 程序集未部署 → headless
        }

        try
        {
            Assembly asm = AssemblyLoadContext.Default.LoadFromAssemblyPath(uiPath);
            Type? bridgeType = asm.GetTypes()
                .FirstOrDefault(t => !t.IsAbstract && typeof(IHostUIBridge).IsAssignableFrom(t));
            return bridgeType == null ? null : (IHostUIBridge)Activator.CreateInstance(bridgeType)!;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "加载 UI 桥失败，以 headless 模式运行");
            return null;
        }
    }
}
