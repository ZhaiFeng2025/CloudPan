namespace CloudPan.Server.UI;

/// <summary>ServerInstaller 部分类：安装流程的进程执行辅助（sc/netsh 直调与 cmd 管道），统一经 ProcessRunner 单点。</summary>
public partial class ServerInstaller
{
    /// <summary>
    /// 直接运行可执行文件（绕过 cmd.exe），经 ProcessRunner 的 ArgumentList 自动转义。
    /// 失败时错误详情通过 LastRunCmdError 存储。
    /// </summary>
    private static bool RunExe(string exeName, params string[] args)
    {
        ProcessResult result = ProcessRunner.Run(exeName, args);
        LastRunCmdError = result.ErrorMessage;
        return result.Success;
    }

    /// <summary>
    /// 仅用于需要 cmd 管道（如 | find）的命令——参数必须硬编码、无用户输入。
    /// 切勿将用户可控的路径/文本传入此方法。
    /// </summary>
    private static bool RunCmd(string command)
    {
        ProcessResult result = ProcessRunner.Run("cmd.exe", null,
            new ProcessRunnerOptions { UseCmd = true, CmdCommand = command });
        LastRunCmdError = result.ErrorMessage;
        return result.Success;
    }

    private static string? LastRunCmdError;

    /// <summary>获取最近一次命令调用的错误详情。</summary>
    private static string? GetLastCmdError() => LastRunCmdError;
}
