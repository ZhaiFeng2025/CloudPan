using System.Diagnostics;

namespace CloudPan.Server.UI;

/// <summary>ServerInstaller 部分类：安装流程的进程执行辅助（sc/netsh 直调与 cmd 管道），统一错误收集。</summary>
public partial class ServerInstaller
{
    /// <summary>
    /// 直接运行可执行文件（绕过 cmd.exe），从根本上消除命令注入风险。
    /// 使用 ProcessStartInfo.ArgumentList 自动处理参数转义。
    /// 失败时错误详情通过 LastRunCmdError 存储。
    /// </summary>
    private static bool RunExe(string exeName, params string[] args)
    {
        LastRunCmdError = null;
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo(exeName)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = false,
                RedirectStandardError = true
            };
            foreach (string a in args)
            {
                psi.ArgumentList.Add(a);
            }

            using Process? p = Process.Start(psi);
            if (p == null)
            {
                LastRunCmdError = $"无法启动进程: {exeName}";
                return false;
            }

            // 异步读取 stderr 避免管道缓冲区满导致子进程死锁。
            // 本地函数捕获局部 stderrOutput，同时满足 CP301（非匿名 lambda，可退订）。
            string? stderrOutput = null;
            void OnErrorDataReceived(object sender, DataReceivedEventArgs e)
            {
                if (e.Data != null)
                {
                    stderrOutput = (stderrOutput ?? "") + e.Data + "\n";
                }
            }
            p.ErrorDataReceived += OnErrorDataReceived;
            p.BeginErrorReadLine();

            if (!p.WaitForExit(30000))
            {
                p.Kill();
                try { p.WaitForExit(1000); } catch { }
                LastRunCmdError = $"命令执行超时(30s): {exeName} {string.Join(" ", args)}";
                return false;
            }

            // 等待异步读取完成（WaitForExit 不保证异步读取已全部到达）
            p.WaitForExit();

            if (p.ExitCode != 0)
            {
                string errTrimmed = !string.IsNullOrWhiteSpace(stderrOutput) ? stderrOutput.Trim() : "(无错误输出)";
                LastRunCmdError = $"exit={p.ExitCode}: {errTrimmed[..Math.Min(errTrimmed.Length, 120)]}";
            }

            return p.ExitCode == 0;
        }
        catch (Exception ex)
        {
            LastRunCmdError = $"{ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// 仅用于需要 cmd 管道（如 | find）的命令——参数必须硬编码、无用户输入。
    /// 切勿将用户可控的路径/文本传入此方法。
    /// </summary>
    private static bool RunCmd(string command)
    {
        LastRunCmdError = null;
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo("cmd.exe", "/c " + command)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = false,
                RedirectStandardError = true
            };
            using Process? p = Process.Start(psi);
            if (p == null)
            {
                return false;
            }

            if (!p.WaitForExit(30000))
            {
                p.Kill();
                try { p.WaitForExit(1000); } catch { }
                LastRunCmdError = $"命令执行超时(30s): {command}";
                return false;
            }

            string stderr = p.StandardError.ReadToEnd();

            if (p.ExitCode != 0)
            {
                string errTrimmed = !string.IsNullOrWhiteSpace(stderr) ? stderr.Trim() : "(无错误输出)";
                LastRunCmdError = $"exit={p.ExitCode}: {errTrimmed[..Math.Min(errTrimmed.Length, 120)]}";
            }

            return p.ExitCode == 0;
        }
        catch (Exception ex)
        {
            LastRunCmdError = $"{ex.Message}";
            return false;
        }
    }

    private static string? LastRunCmdError;

    /// <summary>获取最近一次命令调用的错误详情。</summary>
    private static string? GetLastCmdError() => LastRunCmdError;
}
