using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace MediaServer.Infrastructure.Migrations;

[DbContext(typeof(MediaDb))]
[Migration("20261105120000_MovieMetadata")]
public class MovieMetadata : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.AddColumn<string>(
            name: "MetadataJson",
            table: "Items",
            type: "TEXT",
            nullable: false,
            defaultValue: "null"
        );

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropColumn(name: "MetadataJson", table: "Items");
}
