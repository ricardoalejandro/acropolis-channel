using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Acropolis.Catalog.Infrastructure.Migrations;

public partial class AddTopics : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(name: "Topics", schema: "catalog", columns: table => new
        {
            Id = table.Column<Guid>(type: "uuid", nullable: false),
            Slug = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
            Name = table.Column<string>(type: "character varying(180)", maxLength: 180, nullable: false),
            Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
            Position = table.Column<int>(type: "integer", nullable: false),
            Version = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
            CreatedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
            UpdatedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
        }, constraints: table =>
        {
            table.PrimaryKey("PK_Topics", x => x.Id);
            table.CheckConstraint("CK_Topics_Slug", "length(\"Slug\") BETWEEN 2 AND 160 AND \"Slug\" ~ '^[a-z0-9]+(-[a-z0-9]+)*$'");
            table.CheckConstraint("CK_Topics_Name", "length(btrim(\"Name\")) BETWEEN 2 AND 180");
            table.CheckConstraint("CK_Topics_Status", "\"Status\" IN ('active','archived')");
            table.CheckConstraint("CK_Topics_Position", "\"Position\" >= 0");
            table.CheckConstraint("CK_Topics_Version", "\"Version\" ~ '^[a-f0-9]{32}$'");
        });
        migrationBuilder.CreateTable(name: "TopicDirectory", schema: "catalog", columns: table => new
        {
            Id = table.Column<int>(type: "integer", nullable: false),
            Version = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false)
        }, constraints: table =>
        {
            table.PrimaryKey("PK_TopicDirectory", x => x.Id);
            table.CheckConstraint("CK_TopicDirectory_Id", "\"Id\" = 1");
            table.CheckConstraint("CK_TopicDirectory_Version", "\"Version\" ~ '^[a-f0-9]{32}$'");
        });
        migrationBuilder.CreateTable(name: "TopicAudit", schema: "catalog", columns: table => new
        {
            Id = table.Column<Guid>(type: "uuid", nullable: false),
            ActorId = table.Column<Guid>(type: "uuid", nullable: false),
            TopicId = table.Column<Guid>(type: "uuid", nullable: false),
            Action = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
            Changes = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
            CreatedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
        }, constraints: table => table.PrimaryKey("PK_TopicAudit", x => x.Id));
        migrationBuilder.CreateTable(name: "ContentTopics", schema: "catalog", columns: table => new
        {
            ContentId = table.Column<Guid>(type: "uuid", nullable: false),
            TopicId = table.Column<Guid>(type: "uuid", nullable: false)
        }, constraints: table =>
        {
            table.PrimaryKey("PK_ContentTopics", x => new { x.ContentId, x.TopicId });
            table.ForeignKey(name: "FK_ContentTopics_Contents_ContentId", column: x => x.ContentId, principalSchema: "catalog", principalTable: "Contents", principalColumn: "Id", onDelete: ReferentialAction.Restrict);
            table.ForeignKey(name: "FK_ContentTopics_Topics_TopicId", column: x => x.TopicId, principalSchema: "catalog", principalTable: "Topics", principalColumn: "Id", onDelete: ReferentialAction.Restrict);
        });
        migrationBuilder.CreateIndex("IX_Topics_Slug", "Topics", "Slug", "catalog", unique: true);
        migrationBuilder.CreateIndex("IX_Topics_Position_Id", "Topics", new[] { "Position", "Id" }, "catalog");
        migrationBuilder.CreateIndex("IX_Topics_Status_Position_Id", "Topics", new[] { "Status", "Position", "Id" }, "catalog");
        migrationBuilder.CreateIndex("IX_Topics_Name", "Topics", "Name", "catalog").Annotation("Npgsql:IndexMethod", "gin").Annotation("Npgsql:IndexOperators", new[] { "identity.gin_trgm_ops" });
        migrationBuilder.CreateIndex("IX_ContentTopics_TopicId_ContentId", "ContentTopics", new[] { "TopicId", "ContentId" }, "catalog");
        migrationBuilder.CreateIndex("IX_TopicAudit_TopicId_CreatedUtc_Id", "TopicAudit", new[] { "TopicId", "CreatedUtc", "Id" }, "catalog", descending: new[] { false, true, false });
        // Structural singleton, not editorial seed data. The directory starts with zero topics.
        migrationBuilder.InsertData("TopicDirectory", new[] { "Id", "Version" }, new object[] { 1, "00000000000000000000000000000000" }, "catalog");
        migrationBuilder.Sql("""
            CREATE FUNCTION catalog.enforce_content_topics_limit() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
              PERFORM 1 FROM catalog."Contents" WHERE "Id"=NEW."ContentId" FOR UPDATE;
              IF (SELECT count(*) FROM catalog."ContentTopics" WHERE "ContentId"=NEW."ContentId") > 12 THEN
                RAISE EXCEPTION 'Content topic limit exceeded' USING ERRCODE='23514', CONSTRAINT='CK_ContentTopics_Limit';
              END IF;
              RETURN NEW;
            END $$;
            CREATE CONSTRAINT TRIGGER "CK_ContentTopics_Limit" AFTER INSERT OR UPDATE ON catalog."ContentTopics"
              DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION catalog.enforce_content_topics_limit();
            """);
    }
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("ContentTopics", "catalog");
        migrationBuilder.Sql("DROP FUNCTION catalog.enforce_content_topics_limit()");
        migrationBuilder.DropTable("TopicAudit", "catalog");
        migrationBuilder.DropTable("TopicDirectory", "catalog");
        migrationBuilder.DropTable("Topics", "catalog");
    }
}
