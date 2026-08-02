using BenchmarkDotNet.Running;

namespace CloudPan.Tests.Benchmarks;

/// <summary>
/// 基准测试入口（BenchmarkDotNet 标准模式）。
/// 运行：dotnet run -c Release --project CloudPan.Tests -- --filter "*"
/// 说明：项目为测试项目，`dotnet test` 走 VSTest/testhost 不执行此 Main，仅 `dotnet run` 时生效。
/// </summary>
public static class BenchmarkRunnerMain
{
    public static void Main(string[] args)
    {
        BenchmarkSwitcher.FromAssembly(typeof(BenchmarkRunnerMain).Assembly).Run(args);
    }
}
