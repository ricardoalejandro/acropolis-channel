using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Acropolis.Catalog.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddRestrictedWorks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Audit_ContentId_CreatedUtc",
                schema: "catalog",
                table: "Audit");

            migrationBuilder.AddColumn<string>(
                name: "Author",
                schema: "catalog",
                table: "Contents",
                type: "character varying(180)",
                maxLength: 180,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CollectionKind",
                schema: "catalog",
                table: "Contents",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<Guid[]>(
                name: "ItemIds",
                schema: "catalog",
                table: "Contents",
                type: "uuid[]",
                nullable: false,
                defaultValue: new Guid[0]);

            migrationBuilder.AddColumn<string[]>(
                name: "Tags",
                schema: "catalog",
                table: "Contents",
                type: "character varying(40)[]",
                nullable: false,
                defaultValue: new string[0]);

            migrationBuilder.AddColumn<string>(
                name: "WorkText",
                schema: "catalog",
                table: "Contents",
                type: "character varying(500000)",
                maxLength: 500000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "YouTubeId",
                schema: "catalog",
                table: "Contents",
                type: "character varying(11)",
                maxLength: 11,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_Contents_Collection",
                schema: "catalog",
                table: "Contents",
                sql: "(\"CollectionKind\" IS NULL AND cardinality(\"ItemIds\")=0) OR (\"CollectionKind\" IS NOT NULL AND \"CollectionKind\" IN ('course','program') AND \"Category\"='cursos' AND \"WorkText\" IS NULL AND \"YouTubeId\" IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Contents_CollectionSize",
                schema: "catalog",
                table: "Contents",
                sql: "array_position(\"ItemIds\", NULL) IS NULL AND cardinality(\"ItemIds\") BETWEEN 0 AND 100 AND (\"Status\" <> 'published' OR \"CollectionKind\" IS NULL OR cardinality(\"ItemIds\") > 0)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Contents_Tags",
                schema: "catalog",
                table: "Contents",
                sql: "array_position(\"Tags\", NULL) IS NULL AND cardinality(\"Tags\") BETWEEN 0 AND 12");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Contents_WorkText",
                schema: "catalog",
                table: "Contents",
                sql: "\"WorkText\" IS NULL OR (\"Category\"='lecturas' AND length(btrim(\"WorkText\")) > 0 AND \"CollectionKind\" IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Contents_YouTube",
                schema: "catalog",
                table: "Contents",
                sql: "\"YouTubeId\" IS NULL OR (\"YouTubeId\" ~ '^[A-Za-z0-9_-]{11}$' AND \"Category\" IN ('documentales','videos','podcast','charlas-online') AND \"CollectionKind\" IS NULL)");

            migrationBuilder.CreateIndex(
                name: "IX_Audit_Action_CreatedUtc_Id",
                schema: "catalog",
                table: "Audit",
                columns: new[] { "Action", "CreatedUtc", "Id" },
                descending: new[] { false, true, false });

            migrationBuilder.CreateIndex(
                name: "IX_Audit_ContentId_CreatedUtc_Id",
                schema: "catalog",
                table: "Audit",
                columns: new[] { "ContentId", "CreatedUtc", "Id" },
                descending: new[] { false, true, false });

            migrationBuilder.CreateIndex(
                name: "IX_Audit_CreatedUtc_Id",
                schema: "catalog",
                table: "Audit",
                columns: new[] { "CreatedUtc", "Id" },
                descending: new[] { true, false });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Contents_Collection",
                schema: "catalog",
                table: "Contents");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Contents_CollectionSize",
                schema: "catalog",
                table: "Contents");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Contents_Tags",
                schema: "catalog",
                table: "Contents");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Contents_WorkText",
                schema: "catalog",
                table: "Contents");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Contents_YouTube",
                schema: "catalog",
                table: "Contents");

            migrationBuilder.DropIndex(
                name: "IX_Audit_Action_CreatedUtc_Id",
                schema: "catalog",
                table: "Audit");

            migrationBuilder.DropIndex(
                name: "IX_Audit_ContentId_CreatedUtc_Id",
                schema: "catalog",
                table: "Audit");

            migrationBuilder.DropIndex(
                name: "IX_Audit_CreatedUtc_Id",
                schema: "catalog",
                table: "Audit");

            migrationBuilder.DropColumn(
                name: "Author",
                schema: "catalog",
                table: "Contents");

            migrationBuilder.DropColumn(
                name: "CollectionKind",
                schema: "catalog",
                table: "Contents");

            migrationBuilder.DropColumn(
                name: "ItemIds",
                schema: "catalog",
                table: "Contents");

            migrationBuilder.DropColumn(
                name: "Tags",
                schema: "catalog",
                table: "Contents");

            migrationBuilder.DropColumn(
                name: "WorkText",
                schema: "catalog",
                table: "Contents");

            migrationBuilder.DropColumn(
                name: "YouTubeId",
                schema: "catalog",
                table: "Contents");

            migrationBuilder.CreateIndex(
                name: "IX_Audit_ContentId_CreatedUtc",
                schema: "catalog",
                table: "Audit",
                columns: new[] { "ContentId", "CreatedUtc" });
        }
    }
}
