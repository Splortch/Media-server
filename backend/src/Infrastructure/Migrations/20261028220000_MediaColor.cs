using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace MediaServer.Infrastructure.Migrations;

[DbContext(typeof(MediaDb))]
[Migration("20261028220000_MediaColor")]
public class MediaColor : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.AddColumn<string>(
            name: "ColorJson",
            table: "Items",
            type: "TEXT",
            nullable: false,
            defaultValue: "null"
        );

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropColumn(name: "ColorJson", table: "Items");
}
