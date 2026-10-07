using Acropolis.Identity.Application;
using Acropolis.Subscriptions.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Acropolis.Migrations;

public sealed class SubscriptionOperations(SubscriptionsDbContext database, TimeProvider clock)
{
    public async Task InvalidateRecoveryAsync(CancellationToken token)
    {
        var previousTimeout = database.Database.GetCommandTimeout();
        database.Database.SetCommandTimeout(120);
        try
        {
            await using var transaction = await database.Database.BeginTransactionAsync(token);
            await database.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({IdentityRules.AdministrationLockKey})", token);
            var now = clock.GetUtcNow();
            // Restored messages never replay, including a sent-state snapshot. Tombstones cover terms not queued before the backup.
            await database.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE subscriptions."NotificationOutbox" SET "Status"='cancelled', "LeaseOwner"=NULL, "LeaseExpiresUtc"=NULL, "NextAttemptUtc"={now};
                INSERT INTO subscriptions."NotificationOutbox"
                    ("Id","SubscriptionId","UserId","SourceAuditId","TermGeneration","DeduplicationKey","Kind","Plan","StartsUtc","ExpiresUtc","CreatedUtc","Status","Attempts","NextAttemptUtc","LeaseOwner","LeaseExpiresUtc")
                SELECT gen_random_uuid(), "Id", "UserId", NULL, "NotificationTermGeneration",
                    'expiring:'||replace("Id"::text,'-','')||':'||replace("NotificationTermGeneration"::text,'-',''),
                    'expiring', "Plan", "StartsUtc", "ExpiresUtc", {now}, 'cancelled', 0, {now}, NULL, NULL
                FROM subscriptions."Subscriptions" WHERE "Plan" IN ('probationismo','annual')
                ON CONFLICT ("DeduplicationKey") DO NOTHING;
                UPDATE subscriptions."NotificationDeliveryState" SET "NextSubmissionUtc"={now.AddSeconds(15)} WHERE "Id"=1;
                """, token);
            // A single SQL transaction audits only restored active access and suspends exactly those rows.
            await database.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO subscriptions."Audit" ("Id","SubscriptionId","UserId","ActorId","Action","BeforeStatus","AfterStatus","Reason","CreatedUtc","BeforePlan","AfterPlan","BeforeStartsUtc","AfterStartsUtc","BeforeExpiresUtc","AfterExpiresUtc")
                SELECT gen_random_uuid(), "Id", "UserId", {Guid.Empty}, 'subscription.recovery_suspended', 'active', 'suspended',
                    'Acceso restaurado suspendido; requiere revisión explícita.', {now}, "Plan", "Plan", "StartsUtc", "StartsUtc", "ExpiresUtc", "ExpiresUtc"
                FROM subscriptions."Subscriptions" WHERE "Status"='active';
                UPDATE subscriptions."Subscriptions" SET "Status"='suspended', "UpdatedUtc"={now}, "Version"=replace(gen_random_uuid()::text,'-','') WHERE "Status"='active';
                """, token);
            await transaction.CommitAsync(token);
        }
        finally
        {
            database.Database.SetCommandTimeout(previousTimeout);
        }
    }
}
