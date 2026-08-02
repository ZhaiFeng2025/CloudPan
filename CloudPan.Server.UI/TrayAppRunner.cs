using System.Security.Principal;
using System.ServiceProcess;
using CloudPan.Server.Data;
using CloudPan.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace CloudPan.Server.UI;

/// <summary>
/// 运行模式处理器：Windows Service / headless console / tray GUI。
/// 从 Program.cs 提取。headless（--service 或非交互）不创建任何窗口。
/// </summary>
public static class TrayAppRunner
{
    public static async Task RunAsync(WebApplication app, string[] args)
    {
        bool useTray = args.Contains("--tray");
        bool isService = Environment.UserInteractive == false || args.Contains("--service");

        if (useTray || !isService)
        {
            ApplicationConfiguration.Initialize();
            await RunWithTrayAsync(app);
        }
        else
        {
            // headless：Windows Service 或控制台，无 UI
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
    }

    private static async Task RunWithTrayAsync(WebApplication app)
    {
        // 未安装为服务则显示安装向导（仅管理员）
        bool serviceInstalled = IsServiceInstalled("CloudPanServer");
        if (!serviceInstalled)
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            WindowsPrincipal principal = new WindowsPrincipal(identity);
            bool isAdmin = principal.IsInRole(WindowsBuiltInRole.Administrator);

            if (isAdmin)
            {
                var result = MessageBox.Show(
                    "CloudPan 服务尚未安装为 Windows Service。\n\n" +
                    "点击「是」打开安装向导（推荐，开机自启）。\n" +
                    "点击「否」以独立模式运行（本次会话有效）。",
                    "CloudPan Server — 首次运行",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (result == DialogResult.Yes)
                {
                    ServerInstaller installer = new ServerInstaller();
                    var installResult = installer.ShowDialog();
                    if (installResult == DialogResult.OK)
                    {
                        Log.Information("服务安装成功，当前进程退出。服务已在后台运行。");
                        Log.CloseAndFlush();
                        Environment.Exit(0);
                    }
                    else if (installResult == DialogResult.Abort)
                    {
                        Log.Warning("安装向导异常退出，以独立模式运行");
                        Log.Information("提示：本窗口关闭后服务将停止。建议以管理员身份运行并安装为 Windows Service。");
                    }
                }
                else
                {
                    Log.Information("以独立模式运行（关闭本窗口后服务停止）");
                    Console.WriteLine("以独立模式运行 —— 关闭本窗口后服务停止。");
                }
            }
            else
            {
                Log.Information("非管理员，以独立模式运行（关闭本窗口后服务停止）");
                Console.WriteLine("以独立模式运行 —— 关闭本窗口后服务停止。");
            }
        }

        // 先启动 Web 服务器（非阻塞），等待就绪后再创建 UI。
        // 重要：UI 创建必须在 Application.Run() 同一线程上执行（NotifyIcon 内部窗口句柄线程问题）。
        var serverTask = app.RunAsync();

        // 等待 Web 服务器启动就绪（最多 5 秒）
        bool serverFaulted = false;
        try
        {
            Task readyDelay = Task.Delay(5000);
            var completed = await Task.WhenAny(serverTask, readyDelay);
            serverFaulted = completed == serverTask && serverTask.IsFaulted;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Web 服务启动失败");
            serverFaulted = true;
        }

        var dbFactory = app.Services.GetRequiredService<IDbContextFactory<CloudPanDbContext>>();
        ServerWindow window = new ServerWindow(dbFactory);
        ServerTrayApp tray = new ServerTrayApp(app, window);

        if (serverFaulted)
        {
            var ex = serverTask.Exception!.GetBaseException();
            Log.Fatal(ex, "Web 服务异常退出");
            if (ex.Message.Contains("address already in use", StringComparison.OrdinalIgnoreCase)
                || ex.Message.Contains("Access denied", StringComparison.OrdinalIgnoreCase))
            {
                window.AddLog($"端口 {SpecPorts.HttpPort} 被占用，请检查是否有其他 CloudPan 实例或程序正在使用该端口。");
            }
            else
            {
                window.AddLog($"Web 服务异常退出: {ex.Message}");
            }

            Environment.ExitCode = 1;
        }
        else
        {
            window.AddLog("Web 服务已启动");
        }

        // 注册服务端异常/停止回调（在 UI 线程上安全记录日志）
        _ = serverTask.ContinueWith(t =>
        {
            if (t.IsFaulted)
            {
                var ex = t.Exception!.GetBaseException();
                Log.Fatal(ex, "Web 服务异常退出");
                window.AddLog($"Web 服务异常退出: {ex.Message}");
                Environment.ExitCode = 1;
            }
            else if (t.IsCompletedSuccessfully)
            {
                window.AddLog("Web 服务已正常停止");
            }
        }, TaskContinuationOptions.NotOnCanceled);

        Application.Run(tray);

        // 托盘退出后：等待 Web 服务器停止
        if (!serverTask.IsCompleted)
        {
            try
            {
                await app.StopAsync();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "停止 Web 服务时异常");
            }
        }

        Log.CloseAndFlush();
    }

    private static bool IsServiceInstalled(string serviceName)
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
