using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Acropolis.Catalog.Infrastructure.Migrations;

public partial class AddConsumptionActivity : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(name: "ConsumptionSessions", schema: "catalog", columns: table => new
        {
            Id = table.Column<Guid>(type: "uuid", nullable: false),
            AccountId = table.Column<Guid>(type: "uuid", nullable: false),
            AuthenticationBindingHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
            VisitId = table.Column<Guid>(type: "uuid", nullable: false),
            ContentId = table.Column<Guid>(type: "uuid", nullable: false),
            ContentVersion = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
            CategoryAtStart = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
            SourceKind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
            StartedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
            LastReceivedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
            LastSequence = table.Column<int>(type: "integer", nullable: false),
            CoverageJson = table.Column<string>(type: "jsonb", nullable: false),
            DurationMs = table.Column<int>(type: "integer", nullable: true),
            DurationChanged = table.Column<bool>(type: "boolean", nullable: false),
            CoverageIncomplete = table.Column<bool>(type: "boolean", nullable: false),
            EndedReported = table.Column<bool>(type: "boolean", nullable: false)
        }, constraints: table =>
        {
            table.PrimaryKey("PK_ConsumptionSessions", x => x.Id);
            table.CheckConstraint("CK_ConsumptionSessions_Binding", "\"AuthenticationBindingHash\" ~ '^[a-f0-9]{64}$'");
            table.CheckConstraint("CK_ConsumptionSessions_Version", "\"ContentVersion\" ~ '^[a-f0-9]{32}$'");
            table.CheckConstraint("CK_ConsumptionSessions_Kind", "\"SourceKind\" IN ('reading','youtube')");
            table.CheckConstraint("CK_ConsumptionSessions_Category", "\"CategoryAtStart\" IN ('lecturas','documentales','videos','podcast','charlas-online')");
            table.CheckConstraint("CK_ConsumptionSessions_Sequence", "\"LastSequence\" BETWEEN 0 AND 1000000");
            table.CheckConstraint("CK_ConsumptionSessions_Coverage", "jsonb_typeof(\"CoverageJson\")='array' AND jsonb_array_length(\"CoverageJson\")<=512");
            table.CheckConstraint("CK_ConsumptionSessions_Duration", "\"DurationMs\" IS NULL OR \"DurationMs\" BETWEEN 1 AND 86400000");
            table.CheckConstraint("CK_ConsumptionSessions_Time", "\"LastReceivedUtc\">=\"StartedUtc\"");
            table.ForeignKey(name: "FK_ConsumptionSessions_Contents_ContentId", column: x => x.ContentId, principalSchema: "catalog", principalTable: "Contents", principalColumn: "Id", onDelete: ReferentialAction.Restrict);
        });
        migrationBuilder.CreateIndex("IX_ConsumptionSessions_AccountId_VisitId", "ConsumptionSessions", new[] { "AccountId", "VisitId" }, "catalog", unique: true);
        migrationBuilder.CreateIndex("IX_ConsumptionSessions_AccountId_StartedUtc_Id", "ConsumptionSessions", new[] { "AccountId", "StartedUtc", "Id" }, "catalog");
        migrationBuilder.CreateIndex("IX_ConsumptionSessions_StartedUtc_Id", "ConsumptionSessions", new[] { "StartedUtc", "Id" }, "catalog");
        migrationBuilder.CreateIndex("IX_ConsumptionSessions_ContentId_StartedUtc_Id", "ConsumptionSessions", new[] { "ContentId", "StartedUtc", "Id" }, "catalog");
        migrationBuilder.CreateTable(name: "ConsumptionPulses", schema: "catalog", columns: table => new
        {
            SessionId = table.Column<Guid>(type: "uuid", nullable: false),
            Sequence = table.Column<int>(type: "integer", nullable: false),
            CanonicalHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
            ReceivedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
            CreditedMs = table.Column<int>(type: "integer", nullable: false),
            ProgressBasisPoints = table.Column<int>(type: "integer", nullable: true),
            CoverageIncomplete = table.Column<bool>(type: "boolean", nullable: false),
            EndedReported = table.Column<bool>(type: "boolean", nullable: false),
            EndedNow = table.Column<bool>(type: "boolean", nullable: false)
        }, constraints: table =>
        {
            table.PrimaryKey("PK_ConsumptionPulses", x => new { x.SessionId, x.Sequence });
            table.CheckConstraint("CK_ConsumptionPulses_Sequence", "\"Sequence\" BETWEEN 1 AND 1000000");
            table.CheckConstraint("CK_ConsumptionPulses_Hash", "\"CanonicalHash\" ~ '^[a-f0-9]{64}$'");
            table.CheckConstraint("CK_ConsumptionPulses_Credit", "\"CreditedMs\" BETWEEN 0 AND 15000");
            table.CheckConstraint("CK_ConsumptionPulses_Progress", "\"ProgressBasisPoints\" IS NULL OR \"ProgressBasisPoints\" BETWEEN 0 AND 10000");
            table.CheckConstraint("CK_ConsumptionPulses_End", "NOT \"EndedNow\" OR \"EndedReported\"");
            table.ForeignKey(name: "FK_ConsumptionPulses_ConsumptionSessions_SessionId", column: x => x.SessionId, principalSchema: "catalog", principalTable: "ConsumptionSessions", principalColumn: "Id", onDelete: ReferentialAction.Cascade);
        });
        migrationBuilder.CreateIndex("IX_ConsumptionPulses_ReceivedUtc_SessionId_Sequence", "ConsumptionPulses", new[] { "ReceivedUtc", "SessionId", "Sequence" }, "catalog");
        migrationBuilder.CreateTable(name: "ConsumptionDaily", schema: "catalog", columns: table => new
        {
            DayUtc = table.Column<DateOnly>(type: "date", nullable: false),
            ContentId = table.Column<Guid>(type: "uuid", nullable: false),
            ContentVersion = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
            CategoryAtStart = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
            SourceKind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
            Starts = table.Column<long>(type: "bigint", nullable: false),
            RecordedPulses = table.Column<long>(type: "bigint", nullable: false),
            CreditedMs = table.Column<long>(type: "bigint", nullable: false),
            EndedReports = table.Column<long>(type: "bigint", nullable: false),
            KnownProgressSamples = table.Column<long>(type: "bigint", nullable: false),
            UnknownProgressSamples = table.Column<long>(type: "bigint", nullable: false),
            ProgressBasisPointsSum = table.Column<long>(type: "bigint", nullable: false)
        }, constraints: table =>
        {
            table.PrimaryKey("PK_ConsumptionDaily", x => new { x.DayUtc, x.ContentId, x.ContentVersion, x.CategoryAtStart, x.SourceKind });
            table.CheckConstraint("CK_ConsumptionDaily_Version", "\"ContentVersion\" ~ '^[a-f0-9]{32}$'");
            table.CheckConstraint("CK_ConsumptionDaily_Kind", "\"SourceKind\" IN ('reading','youtube')");
            table.CheckConstraint("CK_ConsumptionDaily_Category", "\"CategoryAtStart\" IN ('lecturas','documentales','videos','podcast','charlas-online')");
            table.CheckConstraint("CK_ConsumptionDaily_Counts", "\"Starts\">=0 AND \"RecordedPulses\">=0 AND \"CreditedMs\">=0 AND \"EndedReports\">=0 AND \"KnownProgressSamples\">=0 AND \"UnknownProgressSamples\">=0 AND \"ProgressBasisPointsSum\">=0 AND \"ProgressBasisPointsSum\"<=\"KnownProgressSamples\"::numeric*10000 AND \"KnownProgressSamples\"+\"UnknownProgressSamples\"=\"RecordedPulses\" AND \"EndedReports\"<=\"RecordedPulses\"");
            table.ForeignKey(name: "FK_ConsumptionDaily_Contents_ContentId", column: x => x.ContentId, principalSchema: "catalog", principalTable: "Contents", principalColumn: "Id", onDelete: ReferentialAction.Restrict);
        });
        migrationBuilder.CreateIndex("IX_ConsumptionDaily_ContentId", "ConsumptionDaily", "ContentId", "catalog");
        migrationBuilder.CreateTable(name: "ConsumptionAccountDaily", schema: "catalog", columns: table => new
        {
            AccountId = table.Column<Guid>(type: "uuid", nullable: false),
            DayUtc = table.Column<DateOnly>(type: "date", nullable: false),
            ContentId = table.Column<Guid>(type: "uuid", nullable: false),
            ContentVersion = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
            CategoryAtStart = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
            SourceKind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
            Starts = table.Column<long>(type: "bigint", nullable: false),
            RecordedPulses = table.Column<long>(type: "bigint", nullable: false),
            CreditedMs = table.Column<long>(type: "bigint", nullable: false),
            EndedReports = table.Column<long>(type: "bigint", nullable: false),
            KnownProgressSamples = table.Column<long>(type: "bigint", nullable: false),
            UnknownProgressSamples = table.Column<long>(type: "bigint", nullable: false),
            ProgressBasisPointsSum = table.Column<long>(type: "bigint", nullable: false)
        }, constraints: table =>
        {
            table.PrimaryKey("PK_ConsumptionAccountDaily", x => new { x.AccountId, x.DayUtc, x.ContentId, x.ContentVersion, x.CategoryAtStart, x.SourceKind });
            table.CheckConstraint("CK_ConsumptionAccountDaily_Version", "\"ContentVersion\" ~ '^[a-f0-9]{32}$'");
            table.CheckConstraint("CK_ConsumptionAccountDaily_Kind", "\"SourceKind\" IN ('reading','youtube')");
            table.CheckConstraint("CK_ConsumptionAccountDaily_Category", "\"CategoryAtStart\" IN ('lecturas','documentales','videos','podcast','charlas-online')");
            table.CheckConstraint("CK_ConsumptionAccountDaily_Counts", "\"Starts\">=0 AND \"RecordedPulses\">=0 AND \"CreditedMs\">=0 AND \"EndedReports\">=0 AND \"KnownProgressSamples\">=0 AND \"UnknownProgressSamples\">=0 AND \"ProgressBasisPointsSum\">=0 AND \"ProgressBasisPointsSum\"<=\"KnownProgressSamples\"::numeric*10000 AND \"KnownProgressSamples\"+\"UnknownProgressSamples\"=\"RecordedPulses\" AND \"EndedReports\"<=\"RecordedPulses\"");
            table.ForeignKey(name: "FK_ConsumptionAccountDaily_Contents_ContentId", column: x => x.ContentId, principalSchema: "catalog", principalTable: "Contents", principalColumn: "Id", onDelete: ReferentialAction.Restrict);
        });
        migrationBuilder.CreateIndex("IX_ConsumptionAccountDaily_ContentId", "ConsumptionAccountDaily", "ContentId", "catalog");
        migrationBuilder.CreateIndex("IX_ConsumptionAccountDaily_DayUtc_AccountId", "ConsumptionAccountDaily", new[] { "DayUtc", "AccountId" }, "catalog");
    }
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("ConsumptionPulses", "catalog");
        migrationBuilder.DropTable("ConsumptionSessions", "catalog");
        migrationBuilder.DropTable("ConsumptionAccountDaily", "catalog");
        migrationBuilder.DropTable("ConsumptionDaily", "catalog");
    }
}
