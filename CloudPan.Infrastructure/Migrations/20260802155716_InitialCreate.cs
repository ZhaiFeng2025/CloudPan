using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CloudPan.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 幂等建表（CREATE ... IF NOT EXISTS）：
            // - 全新库：本迁移建全表 + 索引；
            // - 旧库（EnsureCreated 时代，无 __EFMigrationsHistory）：已存在的表/索引跳过（保留数据），
            //   缺失的表/索引补建——替代原 DatabaseInitializer 手写建表兼容层（ADR-5 / T-008）。
            migrationBuilder.Sql("""
                CREATE TABLE IF NOT EXISTS "AppConfig" (
                    "Key" TEXT NOT NULL CONSTRAINT "PK_AppConfig" PRIMARY KEY,
                    "Value" TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS "ChunkedUpload" (
                    "FilePath" TEXT NOT NULL CONSTRAINT "PK_ChunkedUpload" PRIMARY KEY,
                    "DeviceId" TEXT NOT NULL,
                    "FileHash" TEXT NOT NULL,
                    "TotalChunks" INTEGER NOT NULL,
                    "ReceivedChunks" TEXT NOT NULL,
                    "TempPath" TEXT NOT NULL,
                    "BaseVersion" INTEGER NOT NULL,
                    "LastModified" TEXT NOT NULL,
                    "CreatedAt" TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS "Device" (
                    "Id" TEXT NOT NULL CONSTRAINT "PK_Device" PRIMARY KEY,
                    "Name" TEXT NOT NULL,
                    "Person" TEXT NULL,
                    "LastSeen" TEXT NOT NULL,
                    "Online" INTEGER NOT NULL,
                    "RegisteredAt" TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS "FileEntry" (
                    "Path" TEXT NOT NULL CONSTRAINT "PK_FileEntry" PRIMARY KEY,
                    "Type" INTEGER NOT NULL,
                    "CurrentHash" TEXT NULL,
                    "CurrentSize" INTEGER NOT NULL,
                    "Version" INTEGER NOT NULL,
                    "LastModified" TEXT NOT NULL,
                    "State" INTEGER NOT NULL,
                    "CreatedAt" TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS "Share" (
                    "Id" TEXT NOT NULL CONSTRAINT "PK_Share" PRIMARY KEY,
                    "FilePath" TEXT NOT NULL,
                    "PasswordHash" TEXT NULL,
                    "ExpiresAt" TEXT NULL,
                    "MaxDownloads" INTEGER NULL,
                    "UsedDownloads" INTEGER NOT NULL,
                    "CreatedAt" TEXT NOT NULL,
                    "CreatedBy" TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS "SyncLog" (
                    "Id" INTEGER NOT NULL CONSTRAINT "PK_SyncLog" PRIMARY KEY AUTOINCREMENT,
                    "FilePath" TEXT NOT NULL,
                    "Operation" INTEGER NOT NULL,
                    "DeviceId" TEXT NOT NULL,
                    "Result" INTEGER NOT NULL,
                    "Details" TEXT NULL,
                    "CreatedAt" TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS "VersionRecord" (
                    "Id" INTEGER NOT NULL CONSTRAINT "PK_VersionRecord" PRIMARY KEY AUTOINCREMENT,
                    "FilePath" TEXT NOT NULL,
                    "Version" INTEGER NOT NULL,
                    "Hash" TEXT NOT NULL,
                    "Size" INTEGER NOT NULL,
                    "StoragePath" TEXT NOT NULL,
                    "Timestamp" TEXT NOT NULL,
                    "DeviceId" TEXT NOT NULL,
                    "RestoredFromVersion" INTEGER NULL,
                    CONSTRAINT "FK_VersionRecord_Device_DeviceId" FOREIGN KEY ("DeviceId") REFERENCES "Device" ("Id") ON DELETE CASCADE,
                    CONSTRAINT "FK_VersionRecord_FileEntry_FilePath" FOREIGN KEY ("FilePath") REFERENCES "FileEntry" ("Path") ON DELETE CASCADE
                );

                CREATE INDEX IF NOT EXISTS "IX_ChunkedUpload_CreatedAt" ON "ChunkedUpload" ("CreatedAt");
                CREATE INDEX IF NOT EXISTS "IX_ChunkedUpload_DeviceId" ON "ChunkedUpload" ("DeviceId");
                CREATE INDEX IF NOT EXISTS "IX_Device_LastSeen" ON "Device" ("LastSeen");
                CREATE INDEX IF NOT EXISTS "IX_FileEntry_State" ON "FileEntry" ("State");
                CREATE INDEX IF NOT EXISTS "IX_FileEntry_Type" ON "FileEntry" ("Type");
                CREATE INDEX IF NOT EXISTS "IX_FileEntry_Version" ON "FileEntry" ("Version");
                CREATE INDEX IF NOT EXISTS "IX_Share_CreatedAt" ON "Share" ("CreatedAt");
                CREATE INDEX IF NOT EXISTS "IX_Share_ExpiresAt" ON "Share" ("ExpiresAt");
                CREATE INDEX IF NOT EXISTS "IX_Share_FilePath" ON "Share" ("FilePath");
                CREATE INDEX IF NOT EXISTS "IX_SyncLog_CreatedAt" ON "SyncLog" ("CreatedAt" DESC);
                CREATE INDEX IF NOT EXISTS "IX_SyncLog_FilePath" ON "SyncLog" ("FilePath");
                CREATE INDEX IF NOT EXISTS "idx_version_file" ON "VersionRecord" ("FilePath", "Version");
                CREATE INDEX IF NOT EXISTS "IX_VersionRecord_DeviceId" ON "VersionRecord" ("DeviceId");
                CREATE INDEX IF NOT EXISTS "IX_VersionRecord_FilePath_Version" ON "VersionRecord" ("FilePath", "Version" DESC);
                CREATE INDEX IF NOT EXISTS "IX_VersionRecord_Timestamp" ON "VersionRecord" ("Timestamp");
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AppConfig");

            migrationBuilder.DropTable(
                name: "ChunkedUpload");

            migrationBuilder.DropTable(
                name: "Share");

            migrationBuilder.DropTable(
                name: "SyncLog");

            migrationBuilder.DropTable(
                name: "VersionRecord");

            migrationBuilder.DropTable(
                name: "Device");

            migrationBuilder.DropTable(
                name: "FileEntry");
        }
    }
}
