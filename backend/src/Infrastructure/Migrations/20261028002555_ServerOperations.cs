using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ServerOperations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Name",
                table: "Users",
                type: "TEXT",
                nullable: false,
                collation: "NOCASE",
                oldClrType: typeof(string),
                oldType: "TEXT"
            );

            migrationBuilder.AddColumn<bool>(
                name: "AllLibraries",
                table: "Users",
                type: "INTEGER",
                nullable: false,
                defaultValue: false
            );

            migrationBuilder.AddColumn<bool>(
                name: "Disabled",
                table: "Users",
                type: "INTEGER",
                nullable: false,
                defaultValue: false
            );

            migrationBuilder.AddColumn<int>(
                name: "MaximumRating",
                table: "Users",
                type: "INTEGER",
                nullable: true
            );

            migrationBuilder.AddColumn<bool>(
                name: "ComputeHashes",
                table: "Libraries",
                type: "INTEGER",
                nullable: false,
                defaultValue: false
            );

            migrationBuilder.AddColumn<string>(
                name: "Kind",
                table: "Libraries",
                type: "TEXT",
                nullable: false,
                defaultValue: ""
            );

            migrationBuilder.AddColumn<bool>(
                name: "WatchEnabled",
                table: "Libraries",
                type: "INTEGER",
                nullable: false,
                defaultValue: false
            );

            migrationBuilder.AddColumn<int>(
                name: "ContentRating",
                table: "Items",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0
            );

            migrationBuilder.AddColumn<string>(
                name: "Fingerprint",
                table: "Items",
                type: "TEXT",
                nullable: false,
                defaultValue: ""
            );

            migrationBuilder.AddColumn<bool>(
                name: "IsMissing",
                table: "Items",
                type: "INTEGER",
                nullable: false,
                defaultValue: false
            );

            migrationBuilder.AddColumn<long>(
                name: "ModifiedTicks",
                table: "Items",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L
            );

            migrationBuilder.AddColumn<string>(
                name: "SearchTitle",
                table: "Items",
                type: "TEXT",
                nullable: false,
                defaultValue: ""
            );

            migrationBuilder.CreateTable(
                name: "Collections",
                columns: table => new
                {
                    Id = table
                        .Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Prefix = table.Column<string>(type: "TEXT", nullable: false),
                    LibraryId = table.Column<int>(type: "INTEGER", nullable: true),
                    VideoCodec = table.Column<string>(type: "TEXT", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Collections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Collections_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "Grants",
                columns: table => new
                {
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    LibraryId = table.Column<int>(type: "INTEGER", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Grants", x => new { x.UserId, x.LibraryId });
                    table.ForeignKey(
                        name: "FK_Grants_Libraries_LibraryId",
                        column: x => x.LibraryId,
                        principalTable: "Libraries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade
                    );
                    table.ForeignKey(
                        name: "FK_Grants_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "History",
                columns: table => new
                {
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    MediaItemId = table.Column<int>(type: "INTEGER", nullable: false),
                    Seconds = table.Column<double>(type: "REAL", nullable: false),
                    LastPlayedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_History", x => new { x.UserId, x.MediaItemId });
                    table.ForeignKey(
                        name: "FK_History_Items_MediaItemId",
                        column: x => x.MediaItemId,
                        principalTable: "Items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade
                    );
                    table.ForeignKey(
                        name: "FK_History_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade
                    );
                }
            );

            migrationBuilder.CreateIndex(
                name: "IX_Users_Name",
                table: "Users",
                column: "Name",
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "IX_Items_Fingerprint",
                table: "Items",
                column: "Fingerprint"
            );

            migrationBuilder.CreateIndex(
                name: "IX_Items_SearchTitle_Id",
                table: "Items",
                columns: new[] { "SearchTitle", "Id" }
            );

            migrationBuilder.CreateIndex(
                name: "IX_Collections_UserId",
                table: "Collections",
                column: "UserId"
            );

            migrationBuilder.CreateIndex(
                name: "IX_Grants_LibraryId",
                table: "Grants",
                column: "LibraryId"
            );

            migrationBuilder.CreateIndex(
                name: "IX_History_MediaItemId",
                table: "History",
                column: "MediaItemId"
            );
            migrationBuilder.Sql("UPDATE Users SET AllLibraries=1");
            migrationBuilder.Sql("UPDATE Libraries SET Kind='movies'");
            migrationBuilder.Sql("UPDATE Items SET SearchTitle=lower(Title)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "Collections");

            migrationBuilder.DropTable(name: "Grants");

            migrationBuilder.DropTable(name: "History");

            migrationBuilder.DropIndex(name: "IX_Users_Name", table: "Users");

            migrationBuilder.DropIndex(name: "IX_Items_Fingerprint", table: "Items");

            migrationBuilder.DropIndex(name: "IX_Items_SearchTitle_Id", table: "Items");

            migrationBuilder.DropColumn(name: "AllLibraries", table: "Users");

            migrationBuilder.DropColumn(name: "Disabled", table: "Users");

            migrationBuilder.DropColumn(name: "MaximumRating", table: "Users");

            migrationBuilder.DropColumn(name: "ComputeHashes", table: "Libraries");

            migrationBuilder.DropColumn(name: "Kind", table: "Libraries");

            migrationBuilder.DropColumn(name: "WatchEnabled", table: "Libraries");

            migrationBuilder.DropColumn(name: "ContentRating", table: "Items");

            migrationBuilder.DropColumn(name: "Fingerprint", table: "Items");

            migrationBuilder.DropColumn(name: "IsMissing", table: "Items");

            migrationBuilder.DropColumn(name: "ModifiedTicks", table: "Items");

            migrationBuilder.DropColumn(name: "SearchTitle", table: "Items");

            migrationBuilder.AlterColumn<string>(
                name: "Name",
                table: "Users",
                type: "TEXT",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldCollation: "NOCASE"
            );
        }
    }
}
