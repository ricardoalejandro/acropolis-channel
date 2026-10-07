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
            // A single SQL transaction audits only restored active access and suspends exactly those rows.
            await database.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO subscriptions."Audit" ("Id","SubscriptionId","UserId","ActorId","Action","BeforeStatus","AfterStatus","Reason","CreatedUtc")
                SELECT gen_random_uuid(), "Id", "UserId", {Guid.Empty}, 'subscription.recovery_suspended', 'active', 'suspended',
                    'Acceso restaurado suspendido; requiere revisión explícita.', {now}
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
