using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Acropolis.Identity.Infrastructure.Migrations
{
    public partial class AddAccountAccess : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AccountAccess",
                schema: "identity",
                columns: table => new
                {
                    UserId = table.Column<System.Guid>(type: "uuid", nullable: false),
                    LastSignInUtc = table.Column<System.DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountAccess", x => x.UserId);
                    table.ForeignKey(name: "FK_AccountAccess_Users_UserId", column: x => x.UserId,
                        principalSchema: "identity", principalTable: "Users", principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }
        protected override void Down(MigrationBuilder migrationBuilder) =>
            migrationBuilder.DropTable(name: "AccountAccess", schema: "identity");
    }
}
