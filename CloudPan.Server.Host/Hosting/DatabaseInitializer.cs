using System.Security.Cryptography;
using CloudPan.Server.Data;
using CloudPan.Server.Models;
using CloudPan.Server.Services;
using CloudPan.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CloudPan.Server.Hosting;

/// <summary>
/// 数据库与同步根初始化：建库、完整性检查、WAL、种子数据、家庭 Token 生成。
/// 从 Program.cs 提取，使组合根保持精简（R-A6）。
/// </summary>
public static class DatabaseInitializer
{
    public static void Initialize(IServiceProvider services, string syncRoot)
    {
        // 同步根目录
        var storage = services.GetRequiredService<IFileStorageService>();
        try
        {
            storage.EnsureSyncRootExists();
        }
        catch (Exception ex)
        {
            LogFatal($"同步根目录创建失败:\n{syncRoot}\n\n原因: {ex.Message}\n\n请检查路径是否有效、磁盘是否可用。");
            throw;
        }

        using IServiceScope scope = services.CreateScope();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("CloudPan.Server");
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<CloudPanDbContext>>();
        var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();

        using var db = dbFactory.CreateDbContext();

        // 建库/升级：EF Migrations（初始迁移幂等——全新库建全表，旧库已有表/索引跳过、缺失的表补建）。
        // 迁移取代 EnsureCreated 与手写建表兼容层（ADR-5 / T-008），schema 可演进，旧库数据保留。
        db.Database.Migrate();

        // 启动时 DB 完整性检查
        try
        {
            var integrityRows = db.Database.SqlQueryRaw<string>("PRAGMA integrity_check;").ToList();
            bool ok = integrityRows.Count == 1 && integrityRows[0] == "ok";
            if (!ok)
            {
                string msg = "DB 完整性检查失败: " + string.Join("; ", integrityRows);
                Console.Error.WriteLine(msg);
                throw new InvalidOperationException("请尝试还原备份或删除数据库文件后重新启动。");
            }

            logger.LogInformation("DB 完整性检查: 通过");
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"DB 完整性检查失败(异常): {ex.Message}", ex);
        }

        // WAL 模式 + 外键
        db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
        db.Database.ExecuteSqlRaw("PRAGMA synchronous=NORMAL;");
        db.Database.ExecuteSqlRaw("PRAGMA foreign_keys=ON;");

        // 种子："server" 设备（VersionRecord.DeviceId FK）
        if (!db.Devices.Any(d => d.Id == "server"))
        {
            db.Devices.Add(new Device
            {
                Id = "server",
                Name = "服务端",
                Person = null,
                LastSeen = DateTime.UtcNow.ToString("O"),
                Online = 1,
                RegisteredAt = DateTime.UtcNow.ToString("O")
            });
        }

        // global_version 计数器
        if (!db.AppConfigs.Any(c => c.Key == "global_version"))
        {
            db.AppConfigs.Add(new AppConfig { Key = "global_version", Value = "0" });
        }

        // 首次启动生成家庭共享 Token（仅显示一次）
        if (!db.AppConfigs.Any(c => c.Key == "token_hash"))
        {
            string? presetToken = configuration.GetValue<string>("CloudPan:Token");
            string tokenFile = Path.Combine(syncRoot, ".cloudpan", "token.txt");
            string token;
            if (!string.IsNullOrEmpty(presetToken))
            {
                token = presetToken;
            }
            else
            {
                token = TokenGenerator.Generate();
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine();
                Console.WriteLine("╔══════════════════════════════════════════════════════════════╗");
                Console.WriteLine("║  家庭共享 Token（仅显示一次，请妥善保存）                    ║");
                Console.WriteLine($"║  {token}  ║");
                Console.WriteLine("╠══════════════════════════════════════════════════════════════╣");
                Console.WriteLine($"║  备份文件: {tokenFile,-47} ║");
                Console.WriteLine("║  （安全提示：配置完客户端后请删除此文件）                      ║");
                Console.WriteLine("╚══════════════════════════════════════════════════════════════╝");
                Console.WriteLine();
                Console.ResetColor();
            }

            try
            {
                SecretStore.WriteToken(token, syncRoot);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Token 写入文件失败: {Path}。请手动创建该文件并写入 Token。", tokenFile);
            }

            string tokenHash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
            db.AppConfigs.Add(new AppConfig { Key = "token_hash", Value = tokenHash });
        }

        db.SaveChanges();

        // 重置设备在线状态（避免上次异常退出残留 online）——表名 Device（契约 [Table("Device")]）
        try
        {
            var tableExists = db.Database.SqlQueryRaw<int>(
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='Device';").ToList();
            if (tableExists.Count > 0 && tableExists[0] > 0)
            {
                db.Database.ExecuteSqlRaw("UPDATE Device SET Online = 0");
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "重置设备在线状态失败（非致命）");
        }

        // WAL checkpoint（PASSIVE，失败不截断）
        try { db.Database.ExecuteSqlRaw("PRAGMA wal_checkpoint(PASSIVE);"); }
        catch (Exception ex) { logger.LogWarning(ex, "WAL checkpoint 失败（非致命）"); }
    }

    private static void LogFatal(string message)
    {
        Console.Error.WriteLine($"[启动失败] {message}");
        if (Environment.UserInteractive)
        {
            try { System.Windows.Forms.MessageBox.Show(message, "CloudPan — 启动失败", System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Error); }
            catch { /* 非交互环境忽略 */ }
        }
    }
}
