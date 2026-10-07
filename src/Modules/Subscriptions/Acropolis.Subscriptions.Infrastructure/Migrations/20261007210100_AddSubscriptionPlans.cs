using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Acropolis.Subscriptions.Infrastructure.Migrations;

public partial class AddSubscriptionPlans : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTimeOffset>(name: "StartsUtc", schema: "subscriptions", table: "Subscriptions", type: "timestamp with time zone", nullable: true);
        migrationBuilder.AddColumn<DateTimeOffset>(name: "ExpiresUtc", schema: "subscriptions", table: "Subscriptions", type: "timestamp with time zone", nullable: true);
        migrationBuilder.Sql("UPDATE subscriptions.\"Subscriptions\" SET \"StartsUtc\"=\"ActivatedUtc\"");
        migrationBuilder.AlterColumn<DateTimeOffset>(name: "StartsUtc", schema: "subscriptions", table: "Subscriptions", type: "timestamp with time zone", nullable: false,
            oldClrType: typeof(DateTimeOffset), oldType: "timestamp with time zone", oldNullable: true);
        migrationBuilder.DropCheckConstraint(name: "CK_Subscriptions_Plan", schema: "subscriptions", table: "Subscriptions");
        migrationBuilder.AddCheckConstraint(name: "CK_Subscriptions_Plan", schema: "subscriptions", table: "Subscriptions", sql: "\"Plan\" IN ('free_beta','probationismo','annual')");
        migrationBuilder.AddCheckConstraint(name: "CK_Subscriptions_Terms", schema: "subscriptions", table: "Subscriptions",
            sql: "(\"Plan\"='free_beta' AND \"ExpiresUtc\" IS NULL) OR (\"Plan\" IN ('probationismo','annual') AND \"ExpiresUtc\" IS NOT NULL AND \"ExpiresUtc\">\"StartsUtc\")");
        migrationBuilder.AddColumn<string>(name: "BeforePlan", schema: "subscriptions", table: "Audit", type: "character varying(32)", maxLength: 32, nullable: true);
        migrationBuilder.AddColumn<string>(name: "AfterPlan", schema: "subscriptions", table: "Audit", type: "character varying(32)", maxLength: 32, nullable: true);
        migrationBuilder.AddColumn<DateTimeOffset>(name: "BeforeStartsUtc", schema: "subscriptions", table: "Audit", type: "timestamp with time zone", nullable: true);
        migrationBuilder.AddColumn<DateTimeOffset>(name: "AfterStartsUtc", schema: "subscriptions", table: "Audit", type: "timestamp with time zone", nullable: true);
        migrationBuilder.AddColumn<DateTimeOffset>(name: "BeforeExpiresUtc", schema: "subscriptions", table: "Audit", type: "timestamp with time zone", nullable: true);
        migrationBuilder.AddColumn<DateTimeOffset>(name: "AfterExpiresUtc", schema: "subscriptions", table: "Audit", type: "timestamp with time zone", nullable: true);
    }
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Downgrade cannot discard a manual plan or its term history.
        migrationBuilder.Sql("""
            DO $rollback$
            BEGIN
                IF EXISTS (SELECT 1 FROM subscriptions."Subscriptions" WHERE "Plan"<>'free_beta')
                    OR EXISTS (SELECT 1 FROM subscriptions."Audit" WHERE "BeforePlan"<>'free_beta' OR "AfterPlan"<>'free_beta') THEN
                    RAISE EXCEPTION 'Manual subscription plans or term history prevent downgrade' USING ERRCODE='23514';
                END IF;
            END;
            $rollback$;
            """);
        migrationBuilder.DropCheckConstraint(name: "CK_Subscriptions_Terms", schema: "subscriptions", table: "Subscriptions");
        migrationBuilder.DropCheckConstraint(name: "CK_Subscriptions_Plan", schema: "subscriptions", table: "Subscriptions");
        migrationBuilder.AddCheckConstraint(name: "CK_Subscriptions_Plan", schema: "subscriptions", table: "Subscriptions", sql: "\"Plan\" = 'free_beta'");
        migrationBuilder.DropColumn(name: "StartsUtc", schema: "subscriptions", table: "Subscriptions");
        migrationBuilder.DropColumn(name: "ExpiresUtc", schema: "subscriptions", table: "Subscriptions");
        migrationBuilder.DropColumn(name: "BeforePlan", schema: "subscriptions", table: "Audit");
        migrationBuilder.DropColumn(name: "AfterPlan", schema: "subscriptions", table: "Audit");
        migrationBuilder.DropColumn(name: "BeforeStartsUtc", schema: "subscriptions", table: "Audit");
        migrationBuilder.DropColumn(name: "AfterStartsUtc", schema: "subscriptions", table: "Audit");
        migrationBuilder.DropColumn(name: "BeforeExpiresUtc", schema: "subscriptions", table: "Audit");
        migrationBuilder.DropColumn(name: "AfterExpiresUtc", schema: "subscriptions", table: "Audit");
    }
}
