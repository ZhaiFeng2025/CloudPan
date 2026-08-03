using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CloudPan.Infrastructure.Persistence.Client.Migrations
{
    /// <inheritdoc />
    public partial class AddRemoteSnapshotLastModified : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LastModified",
                table: "RemoteSnapshots",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastModified",
                table: "RemoteSnapshots");
        }
    }
}
