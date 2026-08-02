using CloudPan.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CloudPan.Server.Core;

/// <inheritdoc />
public class ServerStatusService : IServerStatusService
{
    private readonly IDbContextFactory<CloudPanDbContext> _dbFactory;

    public ServerStatusService(IDbContextFactory<CloudPanDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    /// <inheritdoc />
    public async Task<List<FileEntryInfo>> GetFilesAsync(string? path, int limit)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var query = db.FileEntries.AsQueryable();
        if (!string.IsNullOrEmpty(path))
        {
            query = query.Where(f => f.Path.StartsWith(path));
        }

        var items = await query
            .OrderBy(f => f.Path)
            .Take(Math.Min(limit, 1000))
            .Select(f => new FileEntryInfo(f.Path, f.Type, f.CurrentHash, f.CurrentSize, f.Version, f.State, f.LastModified))
            .ToListAsync();
        return items;
    }

    /// <inheritdoc />
    public async Task<List<DeviceInfo>> GetDevicesAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var items = await db.Devices
            .OrderByDescending(d => d.LastSeen)
            .Select(d => new DeviceInfo(d.Id, d.Name, d.Person, d.LastSeen, d.Online, d.RegisteredAt))
            .ToListAsync();
        return items;
    }

    /// <inheritdoc />
    public async Task<List<SyncLogInfo>> GetLogsAsync(int limit)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var items = await db.SyncLogs
            .OrderByDescending(l => l.CreatedAt)
            .Take(Math.Min(limit, 500))
            .Select(l => new SyncLogInfo(l.Id, l.FilePath, l.Operation, l.DeviceId, l.Result, l.Details, l.CreatedAt))
            .ToListAsync();
        return items;
    }

    /// <inheritdoc />
    public async Task<ServerStats> GetStatsAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        int fileCount = await db.FileEntries.CountAsync();
        int deviceCount = await db.Devices.CountAsync();
        int onlineCount = await db.Devices.CountAsync(d => d.Online == 1);
        int logCount = await db.SyncLogs.CountAsync();
        return new ServerStats(fileCount, deviceCount, onlineCount, logCount);
    }

    /// <inheritdoc />
    public async Task<string?> GetCertFingerprintAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        string? fp = await db.AppConfigs
            .Where(c => c.Key == "cert_fingerprint")
            .Select(c => c.Value)
            .FirstOrDefaultAsync();
        return fp;
    }

    /// <inheritdoc />
    public async Task<string> CheckDbIntegrityAsync()
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var integrity = await db.Database.SqlQueryRaw<string>("PRAGMA integrity_check;").ToListAsync();
            return integrity.Count == 1 && integrity[0] == "ok" ? "ok" : "error";
        }
        catch
        {
            return "error";
        }
    }
}
