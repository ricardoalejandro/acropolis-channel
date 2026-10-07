using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Acropolis.Identity.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddProtectedOwner : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsOwner",
                schema: "identity",
                table: "Users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "SubscriptionsManage",
                schema: "identity",
                table: "Users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_Users_IsOwner",
                schema: "identity",
                table: "Users",
                column: "IsOwner",
                unique: true,
                filter: "\"IsOwner\"");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Users_OwnerAuthority",
                schema: "identity",
                table: "Users",
                sql: "NOT \"IsOwner\" OR (\"UsersManage\" AND \"ContentManage\" AND \"SubscriptionsManage\" AND \"EmailConfirmed\" AND NOT \"IsDisabled\" AND NOT \"RevalidationRequired\")");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Users_IsOwner",
                schema: "identity",
                table: "Users");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Users_OwnerAuthority",
                schema: "identity",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "IsOwner",
                schema: "identity",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "SubscriptionsManage",
                schema: "identity",
                table: "Users");
        }
    }
}
