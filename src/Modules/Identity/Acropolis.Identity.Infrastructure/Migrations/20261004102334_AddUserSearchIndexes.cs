using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Acropolis.Identity.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddUserSearchIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:identity.pg_trgm", ",,");

            migrationBuilder.CreateIndex(
                name: "IX_Users_SearchEmail",
                schema: "identity",
                table: "Users",
                column: "NormalizedEmail")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "identity.gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "IX_Users_SearchName",
                schema: "identity",
                table: "Users",
                column: "DisplayName")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "identity.gin_trgm_ops" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Users_SearchEmail",
                schema: "identity",
                table: "Users");

            migrationBuilder.DropIndex(
                name: "IX_Users_SearchName",
                schema: "identity",
                table: "Users");

            migrationBuilder.AlterDatabase()
                .OldAnnotation("Npgsql:PostgresExtension:identity.pg_trgm", ",,");
        }
    }
}
