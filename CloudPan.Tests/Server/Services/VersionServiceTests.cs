using CloudPan.Server.Services;
using Xunit;

namespace CloudPan.Tests.Server.Services;

/// <summary>
/// VersionService 单元测试——验证全局版本号原子递增。
/// </summary>
public class VersionServiceTests : Infrastructure.TestBase
{
    [Fact]
    public async Task NextVersion_首次调用_返回1()
    {
        var dbFactory = CreateServerDbFactory();
        VersionService svc = new VersionService(dbFactory);

        int version = await svc.NextVersionAsync();
        Assert.Equal(1, version);
    }

    [Fact]
    public async Task NextVersion_连续调用_单调递增()
    {
        var dbFactory = CreateServerDbFactory();
        VersionService svc = new VersionService(dbFactory);

        int v1 = await svc.NextVersionAsync();
        int v2 = await svc.NextVersionAsync();
        int v3 = await svc.NextVersionAsync();

        Assert.Equal(1, v1);
        Assert.Equal(2, v2);
        Assert.Equal(3, v3);
    }

    [Fact]
    public async Task GetCurrentVersion_初始状态_返回0()
    {
        var dbFactory = CreateServerDbFactory();
        VersionService svc = new VersionService(dbFactory);

        int version = await svc.GetCurrentVersionAsync();
        Assert.Equal(0, version);
    }

    [Fact]
    public async Task GetCurrentVersion_不递增()
    {
        var dbFactory = CreateServerDbFactory();
        VersionService svc = new VersionService(dbFactory);

        int before = await svc.GetCurrentVersionAsync();
        int after = await svc.GetCurrentVersionAsync();

        Assert.Equal(before, after);
    }
}
