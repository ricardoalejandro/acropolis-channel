using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Acropolis.Catalog.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "catalog");

            migrationBuilder.CreateTable(
                name: "Audit",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ActorId = table.Column<Guid>(type: "uuid", nullable: false),
                    ContentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Action = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Changes = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Audit", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Contents",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Slug = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Title = table.Column<string>(type: "character varying(180)", maxLength: 180, nullable: false),
                    Summary = table.Column<string>(type: "character varying(600)", maxLength: 600, nullable: false),
                    Body = table.Column<string>(type: "character varying(50000)", maxLength: 50000, nullable: false),
                    Category = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CoverAsset = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    DurationSeconds = table.Column<int>(type: "integer", nullable: true),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PublishedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Version = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Contents", x => x.Id);
                    table.CheckConstraint("CK_Contents_Category", "\"Category\" IN ('lecturas','documentales','videos','podcast','charlas-online','cursos')");
                    table.CheckConstraint("CK_Contents_Cover", "\"CoverAsset\" IS NULL OR \"CoverAsset\" IN ('hero-acropolis','editorial-reading','editorial-podcast','editorial-dialogue','editorial-nature')");
                    table.CheckConstraint("CK_Contents_Duration", "\"DurationSeconds\" IS NULL OR \"DurationSeconds\" BETWEEN 1 AND 86400");
                    table.CheckConstraint("CK_Contents_Publication", "\"Status\" <> 'published' OR (\"PublishedUtc\" IS NOT NULL AND length(btrim(\"Summary\")) > 0 AND length(btrim(\"Body\")) > 0)");
                    table.CheckConstraint("CK_Contents_Slug", "length(\"Slug\") BETWEEN 2 AND 160 AND \"Slug\" ~ '^[a-z0-9]+(-[a-z0-9]+)*$'");
                    table.CheckConstraint("CK_Contents_Status", "\"Status\" IN ('draft','published','archived')");
                    table.CheckConstraint("CK_Contents_Title", "length(btrim(\"Title\")) BETWEEN 2 AND 180");
                });

            migrationBuilder.CreateIndex(
                name: "IX_Audit_ContentId_CreatedUtc",
                schema: "catalog",
                table: "Audit",
                columns: new[] { "ContentId", "CreatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Contents_Category_Status_PublishedUtc_Id",
                schema: "catalog",
                table: "Contents",
                columns: new[] { "Category", "Status", "PublishedUtc", "Id" },
                descending: new[] { false, false, true, false });

            migrationBuilder.CreateIndex(
                name: "IX_Contents_Slug",
                schema: "catalog",
                table: "Contents",
                column: "Slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Contents_Status_PublishedUtc_Id",
                schema: "catalog",
                table: "Contents",
                columns: new[] { "Status", "PublishedUtc", "Id" },
                descending: new[] { false, true, false });

            migrationBuilder.CreateIndex(
                name: "IX_Contents_Summary",
                schema: "catalog",
                table: "Contents",
                column: "Summary")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "identity.gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "IX_Contents_Title",
                schema: "catalog",
                table: "Contents",
                column: "Title")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "identity.gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "IX_Contents_UpdatedUtc_Id",
                schema: "catalog",
                table: "Contents",
                columns: new[] { "UpdatedUtc", "Id" },
                descending: new[] { true, false });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Audit",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "Contents",
                schema: "catalog");
        }
    }
}
