using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MediaServer.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class EpisodeTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "RevealEpisodeSpoilers",
                table: "Users",
                type: "INTEGER",
                nullable: false,
                defaultValue: false
            );

            migrationBuilder.CreateTable(
                name: "SeasonWatchUndos",
                columns: table => new
                {
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    Token = table.Column<string>(type: "TEXT", nullable: false),
                    MediaItemId = table.Column<int>(type: "INTEGER", nullable: false),
                    Season = table.Column<int>(type: "INTEGER", nullable: false),
                    Revision = table.Column<string>(type: "TEXT", nullable: false),
                    SnapshotJson = table.Column<string>(type: "TEXT", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SeasonWatchUndos", x => x.UserId);
                    table.ForeignKey(
                        name: "FK_SeasonWatchUndos_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade
                    );
                }
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "SeasonWatchUndos");

            migrationBuilder.DropColumn(name: "RevealEpisodeSpoilers", table: "Users");
        }
    }
}
