using CloudPan.Infrastructure.Persistence;
using CloudPan.Server.Core;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CloudPan.Tests.Server.Services;

/// <summary>
/// ServerStatusService 单元测试——管理面板/设备/健康检查只读查询（脱离 ASP.NET，直接注入领域服务）。
/// </summary>
public class ServerStatusServiceTests : Infrastructure.TestBase
{
    private (ServerStatusService svc, IDbContextFactory<CloudPanDbContext> dbFactory) CreateServiceAsync()
    {
        var dbFactory = CreateServerDbFactory();
        return (new ServerStatusService(dbFactory), dbFactory);
    }

    [Fact]
    public async Task GetDevices_返回种子设备()
    {
        var (svc, _) = CreateServiceAsync();

        var devices = await svc.GetDevicesAsync();

        Assert.NotEmpty(devices);
        Assert.Contains(devices, d => d.Id == "server");
    }

    [Fact]
    public async Task GetStats_返回计数()
    {
        var (svc, _) = CreateServiceAsync();

        var stats = await svc.GetStatsAsync();

        Assert.True(stats.DeviceCount >= 1);
        Assert.True(stats.FileCount >= 0);
        Assert.True(stats.LogCount >= 0);
    }

    [Fact]
    public async Task CheckDbIntegrity_新建库_返回ok()
    {
        var (svc, _) = CreateServiceAsync();

        string result = await svc.CheckDbIntegrityAsync();

        Assert.Equal("ok", result);
    }

    [Fact]
    public async Task GetCertFingerprint_未设置_返回null()
    {
        var (svc, _) = CreateServiceAsync();

        string? fp = await svc.GetCertFingerprintAsync();

        Assert.Null(fp);
    }

    [Fact]
    public async Task GetFiles_按前缀过滤()
    {
        var (svc, dbFactory) = CreateServiceAsync();
        var index = new FileIndexService(dbFactory);
        await index.UpsertFileAsync("/a/one.txt", CloudPan.Contract.FileType.File, "h", 1,
            DateTime.UtcNow.ToString("O"), 1);
        await index.UpsertFileAsync("/b/two.txt", CloudPan.Contract.FileType.File, "h", 1,
            DateTime.UtcNow.ToString("O"), 2);

        var aFiles = await svc.GetFilesAsync("/a/", 100);

        var entry = Assert.Single(aFiles);
        Assert.Equal("/a/one.txt", entry.Path);
    }
}
