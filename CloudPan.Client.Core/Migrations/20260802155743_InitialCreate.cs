using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CloudPan.Client.Core.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 幂等建表（CREATE ... IF NOT EXISTS）：
            // - 全新库：本迁移建全表 + 索引；
            // - 旧库（EnsureCreated 时代，无 __EFMigrationsHistory）：已存在的表跳过（保留数据），
            //   缺失的表/索引补建（T-008 替换客户端 EnsureCreated）。
            // 注意：旧客户端库 SyncQueue 可能缺 TargetPath 列，由运行时 EnsureDbCreated 经 PRAGMA 判断后 ALTER 补列
            // （SQLite ALTER ADD COLUMN 无 IF NOT EXISTS，无法在迁移内条件化，见 T-008 note）。
            migrationBuilder.Sql("""
                CREATE TABLE IF NOT EXISTS "RemoteSnapshots" (
                    "Path" TEXT NOT NULL CONSTRAINT "PK_RemoteSnapshots" PRIMARY KEY,
                    "Type" INTEGER NOT NULL,
                    "Hash" TEXT NULL,
                    "Size" INTEGER NOT NULL,
                    "Version" INTEGER NOT NULL,
                    "State" INTEGER NOT NULL
                );

                CREATE TABLE IF NOT EXISTS "SyncCursor" (
                    "Id" INTEGER NOT NULL CONSTRAINT "PK_SyncCursor" PRIMARY KEY AUTOINCREMENT,
                    "LastMaxVersion" INTEGER NOT NULL,
                    "LastSyncAt" TEXT NULL
                );

                CREATE TABLE IF NOT EXISTS "SyncQueue" (
                    "Id" INTEGER NOT NULL CONSTRAINT "PK_SyncQueue" PRIMARY KEY AUTOINCREMENT,
                    "FilePath" TEXT NOT NULL,
                    "Operation" INTEGER NOT NULL,
                    "Priority" INTEGER NOT NULL,
                    "BaseVersion" INTEGER NULL,
                    "FileSize" INTEGER NULL,
                    "RetryCount" INTEGER NOT NULL,
                    "LastError" TEXT NULL,
                    "TargetPath" TEXT NULL,
                    "CreatedAt" TEXT NOT NULL
                );

                CREATE INDEX IF NOT EXISTS "IX_SyncQueue_Priority_CreatedAt" ON "SyncQueue" ("Priority" DESC, "CreatedAt");
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RemoteSnapshots");

            migrationBuilder.DropTable(
                name: "SyncCursor");

            migrationBuilder.DropTable(
                name: "SyncQueue");
        }
    }
}
