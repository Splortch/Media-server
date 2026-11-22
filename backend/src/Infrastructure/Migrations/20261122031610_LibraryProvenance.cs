using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MediaServer.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class LibraryProvenance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "FirstSeenAt",
                table: "Items",
                type: "TEXT",
                nullable: true
            );

            migrationBuilder.AddColumn<DateTime>(
                name: "LastScannedAt",
                table: "Items",
                type: "TEXT",
                nullable: true
            );

            migrationBuilder.CreateTable(
                name: "MatchReports",
                columns: table => new
                {
                    Id = table
                        .Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    MediaItemId = table.Column<int>(type: "INTEGER", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    Note = table.Column<string>(type: "TEXT", nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MatchReports", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MatchReports_Items_MediaItemId",
                        column: x => x.MediaItemId,
                        principalTable: "Items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade
                    );
                    table.ForeignKey(
                        name: "FK_MatchReports_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade
                    );
                }
            );

            migrationBuilder.CreateIndex(
                name: "IX_MatchReports_MediaItemId",
                table: "MatchReports",
                column: "MediaItemId"
            );

            migrationBuilder.CreateIndex(
                name: "IX_MatchReports_State_Id",
                table: "MatchReports",
                columns: new[] { "State", "Id" }
            );

            migrationBuilder.CreateIndex(
                name: "IX_MatchReports_UserId_MediaItemId",
                table: "MatchReports",
                columns: new[] { "UserId", "MediaItemId" },
                unique: true
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "MatchReports");

            migrationBuilder.DropColumn(name: "FirstSeenAt", table: "Items");

            migrationBuilder.DropColumn(name: "LastScannedAt", table: "Items");
        }
    }
}
