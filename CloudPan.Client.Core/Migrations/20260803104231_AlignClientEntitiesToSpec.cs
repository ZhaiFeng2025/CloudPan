using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CloudPan.Client.Core.Migrations
{
    /// <inheritdoc />
    public partial class AlignClientEntitiesToSpec : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_RemoteSnapshots",
                table: "RemoteSnapshots");

            migrationBuilder.RenameTable(
                name: "RemoteSnapshots",
                newName: "RemoteSnapshot");

            migrationBuilder.RenameIndex(
                name: "IX_SyncQueue_Priority_CreatedAt",
                table: "SyncQueue",
                newName: "idx_queue_sort");

            migrationBuilder.AddPrimaryKey(
                name: "PK_RemoteSnapshot",
                table: "RemoteSnapshot",
                column: "Path");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_RemoteSnapshot",
                table: "RemoteSnapshot");

            migrationBuilder.RenameTable(
                name: "RemoteSnapshot",
                newName: "RemoteSnapshots");

            migrationBuilder.RenameIndex(
                name: "idx_queue_sort",
                table: "SyncQueue",
                newName: "IX_SyncQueue_Priority_CreatedAt");

            migrationBuilder.AddPrimaryKey(
                name: "PK_RemoteSnapshots",
                table: "RemoteSnapshots",
                column: "Path");
        }
    }
}
