using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Acropolis.Subscriptions.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialSubscriptions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "subscriptions");

            migrationBuilder.CreateTable(
                name: "Subscriptions",
                schema: "subscriptions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Plan = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ActivatedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CancelledUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Subscriptions", x => x.Id);
                    table.CheckConstraint("CK_Subscriptions_Plan", "\"Plan\" = 'free_beta'");
                    table.CheckConstraint("CK_Subscriptions_Status", "\"Status\" IN ('active','cancelled','suspended')");
                });

            migrationBuilder.CreateTable(
                name: "Audit",
                schema: "subscriptions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SubscriptionId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActorId = table.Column<Guid>(type: "uuid", nullable: false),
                    Action = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    BeforeStatus = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    AfterStatus = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Reason = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Audit", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Audit_Subscriptions_SubscriptionId",
                        column: x => x.SubscriptionId,
                        principalSchema: "subscriptions",
                        principalTable: "Subscriptions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Audit_CreatedUtc_Id",
                schema: "subscriptions",
                table: "Audit",
                columns: new[] { "CreatedUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Audit_SubscriptionId_CreatedUtc",
                schema: "subscriptions",
                table: "Audit",
                columns: new[] { "SubscriptionId", "CreatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Audit_UserId_CreatedUtc",
                schema: "subscriptions",
                table: "Audit",
                columns: new[] { "UserId", "CreatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Subscriptions_Status_UpdatedUtc_Id",
                schema: "subscriptions",
                table: "Subscriptions",
                columns: new[] { "Status", "UpdatedUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Subscriptions_UpdatedUtc_Id",
                schema: "subscriptions",
                table: "Subscriptions",
                columns: new[] { "UpdatedUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Subscriptions_UserId",
                schema: "subscriptions",
                table: "Subscriptions",
                column: "UserId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Audit",
                schema: "subscriptions");

            migrationBuilder.DropTable(
                name: "Subscriptions",
                schema: "subscriptions");
        }
    }
}
