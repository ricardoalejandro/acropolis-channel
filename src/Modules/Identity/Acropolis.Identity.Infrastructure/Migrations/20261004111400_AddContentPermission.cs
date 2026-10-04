using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Acropolis.Identity.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddContentPermission : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ContentManage",
                schema: "identity",
                table: "Users",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ContentManage",
                schema: "identity",
                table: "Users");
        }
    }
}
