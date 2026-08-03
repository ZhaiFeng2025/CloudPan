using System.Diagnostics;
using System.Text;

namespace CloudPan.Server.UI;

/// <summary>进程执行选项（T-052 单一 ProcessRunner 的统一入参）。</summary>
internal sealed record ProcessRunnerOptions
{
    /// <summary>超时毫秒（默认 30s；sc qc 等快命令可缩短）。</summary>
    public int TimeoutMs { get; init; } = 30000;

    /// <summary>是否经 UAC 提升运行（Verb=runas）。提升模式必须 UseShellExecute=true，无法重定向输出，参数须为常量。</summary>
    public bool RunAsAdmin { get; init; }

    /// <summary>是否重定向并收集 stdout（如 sc qc 读取输出）。stderr 始终收集。</summary>
    public bool RedirectOutput { get; init; }

    /// <summary>是否经 cmd.exe 管道（如 | find）。命令必须硬编码、无用户输入。</summary>
    public bool UseCmd { get; init; }

    /// <summary>cmd 管道命令（UseCmd=true 时生效，整体作为 cmd.exe /c 参数）。</summary>
    public string CmdCommand { get; init; } = "";
}

/// <summary>进程执行结果。</summary>
internal sealed record ProcessResult
{
    /// <summary>进程是否成功启动（runas 下用户取消 UAC 时 false）。</summary>
    public bool Started { get; init; }

    /// <summary>是否超时（超时已 Kill）。</summary>
    public bool TimedOut { get; init; }

    public int ExitCode { get; init; }

    /// <summary>重定向收集的 stdout（RedirectOutput=true 时）。</summary>
    public string? StdOut { get; init; }

    /// <summary>收集的 stderr。</summary>
    public string? StdErr { get; init; }

    /// <summary>失败/超时/启动失败时的人类可读错误详情（stderr 截断 120 字符）。</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>是否视为成功（启动成功、未超时、无错误）。runas 模式仅需 Started 且未超时（不校验退出码，与历史一致）。</summary>
    public bool Success => Started && !TimedOut && ErrorMessage == null;
}

/// <summary>
/// 进程执行统一入口（安全敏感区单点，CLAUDE.md 7.6 反模式 5 命令注入防线）。
/// 统一「启动 → WaitForExit 超时 → Kill → 异步收集 stdout/stderr → 错误截断 120 字符」语义。
/// 参数转义：exe 直调一律经 ArgumentList 自动转义；cmd 管道/runas 参数必须为常量硬编码、无用户输入。
/// </summary>
internal static class ProcessRunner
{
    private const int MaxErrorChars = 120;
    private const int KillGraceMs = 1000;

    /// <summary>默认选项运行可执行文件（ArgumentList 自动转义）。</summary>
    public static ProcessResult Run(string exeName, params string[] args)
        => Run(exeName, args, new ProcessRunnerOptions());

    /// <summary>按选项运行可执行文件。</summary>
    public static ProcessResult Run(string exeName, string[]? args, ProcessRunnerOptions? options = null)
    {
        options ??= new ProcessRunnerOptions();
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo(exeName)
            {
                CreateNoWindow = true,
                UseShellExecute = options.RunAsAdmin
            };

            if (options.UseCmd)
            {
                // cmd 管道：命令整体作为 /c 参数，调用方保证命令硬编码、无用户输入
                psi.Arguments = "/c " + options.CmdCommand;
            }
            else if (options.RunAsAdmin)
            {
                // runas 提升：UseShellExecute=true 不支持 ArgumentList/重定向；参数由调用方保证为常量
                psi.Verb = "runas";
                psi.Arguments = string.Join(" ", args ?? []);
            }
            else
            {
                // 直调 exe：ArgumentList 自动处理特殊字符转义，消除命令注入风险
                foreach (string a in args ?? [])
                {
                    psi.ArgumentList.Add(a);
                }
                psi.RedirectStandardOutput = options.RedirectOutput;
                psi.RedirectStandardError = true;
            }

            using Process? p = Process.Start(psi);
            if (p == null)
            {
                return new ProcessResult { ErrorMessage = $"无法启动进程: {exeName}" };
            }

            // 异步读取输出，避免管道缓冲区满导致子进程死锁（runas 模式下无法重定向）
            StringBuilder? stdout = null;
            StringBuilder? stderr = null;
            if (!options.RunAsAdmin)
            {
                stdout = new StringBuilder();
                stderr = new StringBuilder();
                void OnStdOut(object sender, DataReceivedEventArgs e)
                {
                    if (e.Data != null) stdout.AppendLine(e.Data);
                }
                void OnStdErr(object sender, DataReceivedEventArgs e)
                {
                    if (e.Data != null) stderr.AppendLine(e.Data);
                }
                if (options.RedirectOutput)
                {
                    p.OutputDataReceived += OnStdOut;
                    p.BeginOutputReadLine();
                }
                p.ErrorDataReceived += OnStdErr;
                p.BeginErrorReadLine();
            }

            if (!p.WaitForExit(options.TimeoutMs))
            {
                TryKill(p);
                return new ProcessResult
                {
                    Started = true,
                    TimedOut = true,
                    ErrorMessage = $"命令执行超时({options.TimeoutMs / 1000}s): {Describe(exeName, args, options)}"
                };
            }

            // WaitForExit(ms) 不保证异步读取已全部到达，须再等待一次
            if (!options.RunAsAdmin)
            {
                p.WaitForExit();
            }

            string stdOut = stdout?.ToString().Trim() ?? "";
            string stdErr = stderr?.ToString().Trim() ?? "";
            string? errorMessage = null;
            if (p.ExitCode != 0)
            {
                string errText = !string.IsNullOrWhiteSpace(stdErr) ? stdErr : "(无错误输出)";
                errorMessage = $"exit={p.ExitCode}: {errText[..Math.Min(errText.Length, MaxErrorChars)]}";
            }

            return new ProcessResult
            {
                Started = true,
                ExitCode = p.ExitCode,
                StdOut = stdOut,
                StdErr = stdErr,
                ErrorMessage = errorMessage
            };
        }
        catch (Exception ex)
        {
            return new ProcessResult { ErrorMessage = ex.Message };
        }
    }

    private static void TryKill(Process p)
    {
        // 进程可能已自行退出或尚未真正启动（UAC 提示未响应）
        try { p.Kill(); } catch { }
        try { p.WaitForExit(KillGraceMs); } catch { }
    }

    private static string Describe(string exeName, string[]? args, ProcessRunnerOptions options)
        => options.UseCmd ? options.CmdCommand : $"{exeName} {string.Join(" ", args ?? [])}".Trim();
}
