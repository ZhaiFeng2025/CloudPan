using System.Security.Principal;
using System.ServiceProcess;
using CloudPan.Server.Host.Hosting;

namespace CloudPan.Server.UI;

/// <summary>
/// 服务重启辅助（设置页"重启生效"）。服务模式走 ServiceController / runas 提升 sc；
/// 独立模式重启当前进程重新走 Program.Main（重新读取设置文件）。
/// </summary>
public static class ServiceRestartHelper
{
    private const string ServiceName = "CloudPanServer";

    public static void PromptRestart()
    {
        var result = MessageBox.Show(
            "设置已保存。是否立即重启 CloudPan 使更改生效？",
            "重启服务", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (result != DialogResult.Yes)
        {
            return;
        }

        if (TrayAppRunner.IsServiceInstalled(ServiceName))
        {
            if (IsCurrentUserAdmin())
            {
                RestartServiceDirect();
            }
            else
            {
                RestartServiceElevated();
            }
        }
        else
        {
            // 独立模式：重启当前进程（重新走 Program.Main，重新读取设置）
            Application.Restart();
        }
    }

    private static bool IsCurrentUserAdmin()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        WindowsPrincipal principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static void RestartServiceDirect()
    {
        try
        {
            using var sc = new ServiceController(ServiceName);
            sc.Stop();
            sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
            sc.Start();
            sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
        }
        catch (Exception ex)
        {
            MessageBox.Show($"重启服务失败: {ex.Message}\n请在服务管理器中手动重启。", "CloudPan",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>runas 提升执行 sc stop → sc start（参数为常量服务名，无注入面）。</summary>
    private static void RestartServiceElevated()
    {
        if (!RunElevatedSc("stop", ServiceName))
        {
            return; // 用户取消 UAC
        }
        System.Threading.Thread.Sleep(2000); // 等待停止完成
        RunElevatedSc("start", ServiceName);
    }

    /// <summary>
    /// 检测服务 binPath 是否含指定启动参数残留（如 --SyncRoot/--Port）。
    /// 仅子串检测，不做引号级解析（规避 sc qc 输出解析脆弱性）。
    /// 旧安装的 binPath 带 --SyncRoot 会覆盖 server-settings.json，需提示用户重装迁移。
    /// </summary>
    public static bool ServiceHasLegacyBinPathParam(string param)
    {
        ProcessResult result = ProcessRunner.Run("sc.exe", new[] { "qc", ServiceName },
            new ProcessRunnerOptions { RedirectOutput = true, TimeoutMs = 5000 });
        return result.StdOut?.Contains(param, StringComparison.OrdinalIgnoreCase) ?? false;
    }

    private static bool RunElevatedSc(params string[] args)
    {
        // 参数为常量（stop/start + 服务名），无注入面；runas 模式无法重定向输出，不校验退出码（与历史一致）
        ProcessResult result = ProcessRunner.Run("sc.exe", args,
            new ProcessRunnerOptions { RunAsAdmin = true });
        return result.Started && !result.TimedOut;
    }
}
