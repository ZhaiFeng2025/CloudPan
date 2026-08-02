namespace CloudPan.Server.UI;

/// <summary>
/// 空入口：本程序集作为 UI 库被 Host 引用（WinExe 为满足 WinForms 项目 OutputType 要求的占位入口，不实际启动）。
/// </summary>
internal static class ProgramEntry
{
    [STAThread]
    private static void Main()
    {
        // 该程序集不作为独立入口运行，Host 通过 ProjectReference 引用其中的窗口/托盘类型。
    }
}
