using Acropolis.Identity.Application;
using Acropolis.Subscriptions.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Acropolis.Subscriptions.Infrastructure;

public sealed class SubscriptionNotificationScheduler(SubscriptionsDbContext database, IOptions<SubscriptionNotificationOptions> settings, ITransactionalEmailSender sender, TimeProvider clock)
{
    public Task<int> ScheduleAsync(CancellationToken token)
    {
        if (!settings.Value.Enabled || !sender.IsAvailable) return Task.FromResult(0);
        var now = clock.GetUtcNow();
        var until = now.Add(SubscriptionNotificationRules.ReminderWindow);
        return database.Database.ExecuteSqlInterpolatedAsync($"""
            WITH due AS (
                SELECT s.* FROM subscriptions."Subscriptions" s
                WHERE s."Status"='active' AND s."Plan" IN ('probationismo','annual')
                    AND s."StartsUtc"<={now} AND s."ExpiresUtc">{now} AND s."ExpiresUtc"<={until}
                    AND NOT EXISTS (SELECT 1 FROM subscriptions."NotificationOutbox" n
                        WHERE n."SubscriptionId"=s."Id" AND n."Kind"='expiring' AND n."TermGeneration"=s."NotificationTermGeneration")
                ORDER BY s."ExpiresUtc", s."Id" LIMIT {SubscriptionNotificationRules.BatchSize} FOR SHARE OF s SKIP LOCKED
            )
            INSERT INTO subscriptions."NotificationOutbox"
                ("Id","SubscriptionId","UserId","SourceAuditId","TermGeneration","DeduplicationKey","Kind","Plan","StartsUtc","ExpiresUtc","CreatedUtc","Status","Attempts","NextAttemptUtc","LeaseOwner","LeaseExpiresUtc")
            SELECT gen_random_uuid(), "Id", "UserId", NULL, "NotificationTermGeneration",
                'expiring:'||replace("Id"::text,'-','')||':'||replace("NotificationTermGeneration"::text,'-',''),
                'expiring', "Plan", "StartsUtc", "ExpiresUtc", {now}, 'pending', 0, {now}, NULL, NULL FROM due
            ON CONFLICT ("DeduplicationKey") DO NOTHING
            """, token);
    }
}
