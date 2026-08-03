using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CloudPan.Client.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddRemoteSnapshotIsDownloaded : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 存量快照默认视为「已落盘」（T-037 前只有下载完成/上传成功的快照保留），
            // 保持存量库删除传播行为不回退；下载窗口保护针对升级后新建的快照（IsDownloaded=false）。
            migrationBuilder.AddColumn<bool>(
                name: "IsDownloaded",
                table: "RemoteSnapshots",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsDownloaded",
                table: "RemoteSnapshots");
        }
    }
}
