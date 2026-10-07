using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Acropolis.Subscriptions.Infrastructure.Migrations;

public partial class AddSubscriptionNotifications : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(name: "NotificationTermGeneration", schema: "subscriptions", table: "Subscriptions", type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()");
        migrationBuilder.CreateTable(name: "NotificationDeliveryState", schema: "subscriptions", columns: table => new
        {
            Id = table.Column<int>(type: "integer", nullable: false),
            NextSubmissionUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
        }, constraints: table =>
        {
            table.PrimaryKey("PK_NotificationDeliveryState", x => x.Id);
            table.CheckConstraint("CK_NotificationDeliveryState_Id", "\"Id\"=1");
        });
        migrationBuilder.Sql("INSERT INTO subscriptions.\"NotificationDeliveryState\" (\"Id\",\"NextSubmissionUtc\") VALUES (1,TIMESTAMPTZ '2000-01-01 00:00:00+00')");
        migrationBuilder.CreateTable(name: "NotificationOutbox", schema: "subscriptions", columns: table => new
        {
            Id = table.Column<Guid>(type: "uuid", nullable: false),
            SubscriptionId = table.Column<Guid>(type: "uuid", nullable: false),
            UserId = table.Column<Guid>(type: "uuid", nullable: false),
            SourceAuditId = table.Column<Guid>(type: "uuid", nullable: true),
            TermGeneration = table.Column<Guid>(type: "uuid", nullable: false),
            DeduplicationKey = table.Column<string>(type: "character varying(96)", maxLength: 96, nullable: false),
            Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
            Plan = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
            StartsUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
            ExpiresUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
            CreatedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
            Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
            Attempts = table.Column<int>(type: "integer", nullable: false),
            NextAttemptUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
            LeaseOwner = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
            LeaseExpiresUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
        }, constraints: table =>
        {
            table.PrimaryKey("PK_NotificationOutbox", x => x.Id);
            table.CheckConstraint("CK_NotificationOutbox_Kind", "\"Kind\" IN ('assigned','renewed','expiring')");
            table.CheckConstraint("CK_NotificationOutbox_Status", "\"Status\" IN ('pending','sending','sent','failed','cancelled')");
            table.CheckConstraint("CK_NotificationOutbox_Attempts", "\"Attempts\" BETWEEN 0 AND 5");
            table.CheckConstraint("CK_NotificationOutbox_Source", "(\"Kind\"='expiring' AND \"SourceAuditId\" IS NULL) OR (\"Kind\" IN ('assigned','renewed') AND \"SourceAuditId\" IS NOT NULL)");
            table.CheckConstraint("CK_NotificationOutbox_Reminder", "\"Kind\"<>'expiring' OR \"ExpiresUtc\" IS NOT NULL");
            table.CheckConstraint("CK_NotificationOutbox_Lease", "(\"Status\"='sending' AND \"LeaseOwner\" IS NOT NULL AND \"LeaseExpiresUtc\" IS NOT NULL) OR (\"Status\"<>'sending' AND \"LeaseOwner\" IS NULL AND \"LeaseExpiresUtc\" IS NULL)");
            table.CheckConstraint("CK_NotificationOutbox_Terms", "(\"Plan\"='free_beta' AND \"ExpiresUtc\" IS NULL) OR (\"Plan\" IN ('probationismo','annual') AND \"ExpiresUtc\" IS NOT NULL AND \"ExpiresUtc\">\"StartsUtc\")");
            table.ForeignKey(name: "FK_NotificationOutbox_Subscriptions_SubscriptionId", column: x => x.SubscriptionId, principalSchema: "subscriptions", principalTable: "Subscriptions", principalColumn: "Id", onDelete: ReferentialAction.Restrict);
            table.ForeignKey(name: "FK_NotificationOutbox_Audit_SourceAuditId", column: x => x.SourceAuditId, principalSchema: "subscriptions", principalTable: "Audit", principalColumn: "Id", onDelete: ReferentialAction.Restrict);
        });
        migrationBuilder.CreateIndex(name: "IX_Subscriptions_Status_ExpiresUtc_Id", schema: "subscriptions", table: "Subscriptions", columns: new[] { "Status", "ExpiresUtc", "Id" });
        migrationBuilder.CreateIndex(name: "IX_NotificationOutbox_DeduplicationKey", schema: "subscriptions", table: "NotificationOutbox", column: "DeduplicationKey", unique: true);
        migrationBuilder.CreateIndex(name: "IX_NotificationOutbox_SourceAuditId", schema: "subscriptions", table: "NotificationOutbox", column: "SourceAuditId", unique: true);
        migrationBuilder.CreateIndex(name: "IX_NotificationOutbox_Status_NextAttemptUtc_Id", schema: "subscriptions", table: "NotificationOutbox", columns: new[] { "Status", "NextAttemptUtc", "Id" });
        migrationBuilder.CreateIndex(name: "IX_NotificationOutbox_SubscriptionId_Kind_TermGeneration", schema: "subscriptions", table: "NotificationOutbox", columns: new[] { "SubscriptionId", "Kind", "TermGeneration" });
    }
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DO $rollback$ BEGIN IF EXISTS (SELECT 1 FROM subscriptions.\"NotificationOutbox\") THEN RAISE EXCEPTION 'Subscription notification history prevents downgrade' USING ERRCODE='23514'; END IF; END; $rollback$;");
        migrationBuilder.DropTable(name: "NotificationOutbox", schema: "subscriptions");
        migrationBuilder.DropTable(name: "NotificationDeliveryState", schema: "subscriptions");
        migrationBuilder.DropIndex(name: "IX_Subscriptions_Status_ExpiresUtc_Id", schema: "subscriptions", table: "Subscriptions");
        migrationBuilder.DropColumn(name: "NotificationTermGeneration", schema: "subscriptions", table: "Subscriptions");
    }
}
