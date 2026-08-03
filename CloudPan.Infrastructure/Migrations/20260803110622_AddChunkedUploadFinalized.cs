using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CloudPan.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddChunkedUploadFinalized : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // T-064：ChunkedUpload 新增 Finalized 列（Finalize 完成标记）。
            // 手工收窄为仅加列：EF 自动生成的额外索引重建（idx_log_time / idx_version_file 降序）属
            // 模型快照与 InitialCreate 手写 SQL 的既有基线差异，非本任务范围，且会在 EnsureCreated 时代
            // 旧库（索引已存在）上执行 CreateIndex 失败，故不纳入本迁移。
            migrationBuilder.AddColumn<bool>(
                name: "Finalized",
                table: "ChunkedUpload",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Finalized",
                table: "ChunkedUpload");
        }
    }
}
