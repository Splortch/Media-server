using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ClientApi : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CapabilitiesJson",
                table: "Sessions",
                type: "TEXT",
                nullable: false,
                defaultValue: ""
            );

            migrationBuilder.AddColumn<DateTime>(
                name: "CreatedAt",
                table: "Sessions",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified)
            );

            migrationBuilder.AddColumn<string>(
                name: "DeviceName",
                table: "Sessions",
                type: "TEXT",
                nullable: false,
                defaultValue: ""
            );

            migrationBuilder.AddColumn<bool>(
                name: "Completed",
                table: "Progress",
                type: "INTEGER",
                nullable: false,
                defaultValue: false
            );

            migrationBuilder.AddColumn<long>(
                name: "Revision",
                table: "Progress",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L
            );

            migrationBuilder.AddColumn<string>(
                name: "TracksJson",
                table: "Items",
                type: "TEXT",
                nullable: false,
                defaultValue: ""
            );

            migrationBuilder.CreateTable(
                name: "Reports",
                columns: table => new
                {
                    Id = table
                        .Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    MediaItemId = table.Column<int>(type: "INTEGER", nullable: false),
                    Code = table.Column<string>(type: "TEXT", nullable: false),
                    Client = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Reports", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Reports_Items_MediaItemId",
                        column: x => x.MediaItemId,
                        principalTable: "Items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade
                    );
                    table.ForeignKey(
                        name: "FK_Reports_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade
                    );
                }
            );

            migrationBuilder.CreateIndex(
                name: "IX_Reports_MediaItemId",
                table: "Reports",
                column: "MediaItemId"
            );

            migrationBuilder.CreateIndex(
                name: "IX_Reports_UserId",
                table: "Reports",
                column: "UserId"
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "Reports");

            migrationBuilder.DropColumn(name: "CapabilitiesJson", table: "Sessions");

            migrationBuilder.DropColumn(name: "CreatedAt", table: "Sessions");

            migrationBuilder.DropColumn(name: "DeviceName", table: "Sessions");

            migrationBuilder.DropColumn(name: "Completed", table: "Progress");

            migrationBuilder.DropColumn(name: "Revision", table: "Progress");

            migrationBuilder.DropColumn(name: "TracksJson", table: "Items");
        }
    }
}
