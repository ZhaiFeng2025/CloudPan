using System.ServiceProcess;
using Microsoft.AspNetCore.Builder;
using Serilog;

namespace CloudPan.Server.Hosting;

/// <summary>
/// 运行模式处理器：Windows Service / headless console / tray GUI（T-015 从 Server.UI 移入）。
/// headless（--service 或非交互）不创建任何窗口；tray GUI 经 IHostUIBridge 委托给可选 UI 模块。
/// </summary>
public static class TrayAppRunner
{
    public static async Task RunAsync(WebApplication app, string[] args)
    {
        bool useTray = args.Contains("--tray");
        bool isService = Environment.UserInteractive == false || args.Contains("--service");

        if (useTray || !isService)
        {
            IHostUIBridge? bridge = UIBridgeLocator.Find();
            if (bridge != null)
            {
                await bridge.RunTrayAsync(app, args);
                return;
            }

            // 请求了 UI 但 Server.UI 程序集未部署（headless 部署）→ 回退 headless，Kestrel 正常服务
            Log.Warning("未找到 UI 桥实现（CloudPan.Server.UI 未部署），以 headless 模式运行");
        }

        // headless：Windows Service 或控制台，无 UI（app.Run() 阻塞运行至停止，异常由 try-catch 捕获）
        try
        {
            app.Run();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Web 服务运行异常");
            Environment.ExitCode = 1;
        }

        Log.CloseAndFlush();
    }

    /// <summary>检查 Windows 服务是否已安装（供 Server.UI 设置页"重启服务"分支与托盘首启向导复用）。</summary>
    public static bool IsServiceInstalled(string serviceName)
    {
        try
        {
            using var sc = ServiceController.GetServices()
                .FirstOrDefault(s => s.ServiceName == serviceName);
            return sc != null;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "检查服务 {ServiceName} 安装状态时发生异常（可能是权限不足）", serviceName);
            return false;
        }
    }
}
