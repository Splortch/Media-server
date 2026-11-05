using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MediaServer.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class CatalogRightsProvenance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CatalogAttribution",
                table: "Downloads",
                type: "TEXT",
                nullable: true
            );

            migrationBuilder.AddColumn<string>(
                name: "ExpectedSha1",
                table: "Downloads",
                type: "TEXT",
                nullable: true
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "CatalogAttribution", table: "Downloads");

            migrationBuilder.DropColumn(name: "ExpectedSha1", table: "Downloads");
        }
    }
}
