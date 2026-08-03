using CloudPan.Contract;
using Xunit;

namespace CloudPan.Tests.Architecture;

/// <summary>
/// SpecRoutes（ApiClientGenerator 生成的路由常量）与 SpecEndpoints（ManifestGenerator 生成的端点注册表）
/// 一致性测试。两者同源于 shared-spec.json → api.endpoints；
/// 若改 spec 端点后只重生成其一（或漏生成），此测试即失败，兜底 --verify。
/// </summary>
public class SpecRoutesConsistencyTests
{
    [Fact]
    public void SpecRoutes_每条路由常量_均已在SpecEndpoints注册()
    {
        List<string> diffs = SpecRoutes.DiffAgainstSpecEndpoints();
        Assert.Empty(diffs);
    }
}
