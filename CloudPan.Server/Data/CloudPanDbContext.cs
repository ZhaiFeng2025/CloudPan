using Microsoft.EntityFrameworkCore;
using CloudPan.Shared;
using CloudPan.Server.Models;

namespace CloudPan.Server.Data;

/// <summary>
/// CloudPan 服务端数据库上下文。
/// SQLite + WAL 模式，EF Core Code-First。
/// </summary>
public class CloudPanDbContext : DbContext
{
    public DbSet<FileEntry> FileEntries => Set<FileEntry>();
    public DbSet<VersionRecord> VersionRecords => Set<VersionRecord>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<Share> Shares => Set<Share>();
    public DbSet<SyncLog> SyncLogs => Set<SyncLog>();
    public DbSet<ChunkedUpload> ChunkedUploads => Set<ChunkedUpload>();
    public DbSet<AppConfig> AppConfigs => Set<AppConfig>();

    public CloudPanDbContext(DbContextOptions<CloudPanDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder model)
    {
        // FileEntry
        model.Entity<FileEntry>(e =>
        {
            e.HasKey(f => f.Path);
            e.HasIndex(f => f.State);
            e.HasIndex(f => f.Type);
            e.HasIndex(f => f.Version);
        });

        // VersionRecord
        model.Entity<VersionRecord>(e =>
        {
            e.HasKey(v => v.Id);
            e.HasIndex(v => new { v.FilePath, v.Version }).IsDescending(false, true);
            e.HasIndex(v => v.DeviceId);
            e.HasIndex(v => v.Timestamp);
            e.HasOne<FileEntry>().WithMany().HasForeignKey(v => v.FilePath).HasPrincipalKey(f => f.Path);
            e.HasOne<Device>().WithMany().HasForeignKey(v => v.DeviceId).HasPrincipalKey(d => d.Id);
        });

        // Device
        model.Entity<Device>(e =>
        {
            e.HasKey(d => d.Id);
            e.HasIndex(d => d.LastSeen);
        });

        // Share
        model.Entity<Share>(e =>
        {
            e.HasKey(s => s.Id);
            e.HasIndex(s => s.FilePath);
            e.HasIndex(s => s.ExpiresAt);
            e.HasIndex(s => s.CreatedAt);
        });

        // SyncLog
        model.Entity<SyncLog>(e =>
        {
            e.HasKey(l => l.Id);
            e.HasIndex(l => l.CreatedAt).IsDescending();
            e.HasIndex(l => l.FilePath);
        });

        // ChunkedUpload
        model.Entity<ChunkedUpload>(e =>
        {
            e.HasKey(c => c.FilePath);
            e.HasIndex(c => c.DeviceId);
            e.HasIndex(c => c.CreatedAt);
        });

        // AppConfig
        model.Entity<AppConfig>(e =>
        {
            e.HasKey(c => c.Key);
        });
    }
}
