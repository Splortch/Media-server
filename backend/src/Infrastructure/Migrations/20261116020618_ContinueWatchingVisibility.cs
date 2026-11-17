using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MediaServer.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ContinueWatchingVisibility : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "HiddenFromContinueWatching",
                table: "Progress",
                type: "INTEGER",
                nullable: false,
                defaultValue: false
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "HiddenFromContinueWatching", table: "Progress");
        }
    }
}
