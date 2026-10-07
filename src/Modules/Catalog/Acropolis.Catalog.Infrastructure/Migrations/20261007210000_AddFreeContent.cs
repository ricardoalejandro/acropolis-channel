using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Acropolis.Catalog.Infrastructure.Migrations;

public partial class AddFreeContent : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.AddColumn<bool>(name: "IsFree", schema: "catalog", table: "Contents", type: "boolean", nullable: false, defaultValue: false);
    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropColumn(name: "IsFree", schema: "catalog", table: "Contents");
}
