using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MediaServer.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class FamilyDownloadImports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ImportJobId",
                table: "Items",
                type: "TEXT",
                nullable: true
            );

            migrationBuilder.AddColumn<bool>(
                name: "PendingImport",
                table: "Items",
                type: "INTEGER",
                nullable: false,
                defaultValue: false
            );

            migrationBuilder.AddColumn<string>(
                name: "Classification",
                table: "Downloads",
                type: "TEXT",
                nullable: true
            );

            migrationBuilder.AddColumn<int>(
                name: "Episode",
                table: "Downloads",
                type: "INTEGER",
                nullable: true
            );

            migrationBuilder.AddColumn<long>(
                name: "ImportCopiedBytes",
                table: "Downloads",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L
            );

            migrationBuilder.AddColumn<string>(
                name: "ImportError",
                table: "Downloads",
                type: "TEXT",
                nullable: true
            );

            migrationBuilder.AddColumn<string>(
                name: "ImportExtension",
                table: "Downloads",
                type: "TEXT",
                nullable: true
            );

            migrationBuilder.AddColumn<string>(
                name: "ImportNonce",
                table: "Downloads",
                type: "TEXT",
                nullable: true
            );

            migrationBuilder.AddColumn<int>(
                name: "ImportRating",
                table: "Downloads",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0
            );

            migrationBuilder.AddColumn<string>(
                name: "ImportRelativePath",
                table: "Downloads",
                type: "TEXT",
                nullable: true
            );

            migrationBuilder.AddColumn<int>(
                name: "ImportScanId",
                table: "Downloads",
                type: "INTEGER",
                nullable: true
            );

            migrationBuilder.AddColumn<string>(
                name: "ImportState",
                table: "Downloads",
                type: "TEXT",
                nullable: false,
                defaultValue: ""
            );

            migrationBuilder.AddColumn<string>(
                name: "ImportTitle",
                table: "Downloads",
                type: "TEXT",
                nullable: true
            );

            migrationBuilder.AddColumn<int>(
                name: "ImportYear",
                table: "Downloads",
                type: "INTEGER",
                nullable: true
            );

            migrationBuilder.AddColumn<int>(
                name: "ImportedMediaId",
                table: "Downloads",
                type: "INTEGER",
                nullable: true
            );

            migrationBuilder.AddColumn<int>(
                name: "RequestId",
                table: "Downloads",
                type: "INTEGER",
                nullable: true
            );

            migrationBuilder.AddColumn<int>(
                name: "RequestOwnerId",
                table: "Downloads",
                type: "INTEGER",
                nullable: true
            );

            migrationBuilder.AddColumn<long>(
                name: "RequestRevision",
                table: "Downloads",
                type: "INTEGER",
                nullable: true
            );

            migrationBuilder.AddColumn<int>(
                name: "Season",
                table: "Downloads",
                type: "INTEGER",
                nullable: true
            );

            migrationBuilder.AddColumn<string>(
                name: "Series",
                table: "Downloads",
                type: "TEXT",
                nullable: true
            );

            migrationBuilder.AddColumn<int>(
                name: "TargetLibraryId",
                table: "Downloads",
                type: "INTEGER",
                nullable: true
            );

            migrationBuilder.AddColumn<string>(
                name: "TargetRoot",
                table: "Downloads",
                type: "TEXT",
                nullable: true
            );

            migrationBuilder.CreateIndex(
                name: "IX_Downloads_RequestId",
                table: "Downloads",
                column: "RequestId",
                unique: true
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(name: "IX_Downloads_RequestId", table: "Downloads");

            migrationBuilder.DropColumn(name: "ImportJobId", table: "Items");

            migrationBuilder.DropColumn(name: "PendingImport", table: "Items");

            migrationBuilder.DropColumn(name: "Classification", table: "Downloads");

            migrationBuilder.DropColumn(name: "Episode", table: "Downloads");

            migrationBuilder.DropColumn(name: "ImportCopiedBytes", table: "Downloads");

            migrationBuilder.DropColumn(name: "ImportError", table: "Downloads");

            migrationBuilder.DropColumn(name: "ImportExtension", table: "Downloads");

            migrationBuilder.DropColumn(name: "ImportNonce", table: "Downloads");

            migrationBuilder.DropColumn(name: "ImportRating", table: "Downloads");

            migrationBuilder.DropColumn(name: "ImportRelativePath", table: "Downloads");

            migrationBuilder.DropColumn(name: "ImportScanId", table: "Downloads");

            migrationBuilder.DropColumn(name: "ImportState", table: "Downloads");

            migrationBuilder.DropColumn(name: "ImportTitle", table: "Downloads");

            migrationBuilder.DropColumn(name: "ImportYear", table: "Downloads");

            migrationBuilder.DropColumn(name: "ImportedMediaId", table: "Downloads");

            migrationBuilder.DropColumn(name: "RequestId", table: "Downloads");

            migrationBuilder.DropColumn(name: "RequestOwnerId", table: "Downloads");

            migrationBuilder.DropColumn(name: "RequestRevision", table: "Downloads");

            migrationBuilder.DropColumn(name: "Season", table: "Downloads");

            migrationBuilder.DropColumn(name: "Series", table: "Downloads");

            migrationBuilder.DropColumn(name: "TargetLibraryId", table: "Downloads");

            migrationBuilder.DropColumn(name: "TargetRoot", table: "Downloads");
        }
    }
}
