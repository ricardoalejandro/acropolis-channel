using Acropolis.Subscriptions.Application;
using Microsoft.EntityFrameworkCore;

namespace Acropolis.Subscriptions.Infrastructure;

public sealed class SubscriptionReportService(SubscriptionsDbContext database, TimeProvider clock) : ISubscriptionReportService, ISubscriptionEventReportService
{
    public async Task<SubscriptionReportView> GetCurrentAsync(CancellationToken token)
    {
        var generatedUtc = clock.GetUtcNow().ToUniversalTime();
        var groups = await database.Subscriptions.AsNoTracking().GroupBy(x => new
        {
            x.Status,
            Effective = x.Status != "active" ? x.Status : generatedUtc < x.StartsUtc ? "scheduled" :
                    x.ExpiresUtc != null && generatedUtc >= x.ExpiresUtc ? "expired" : "active"
        })
            .Select(x => new { x.Key.Status, x.Key.Effective, Count = x.LongCount() }).ToArrayAsync(token);
        var counts = groups.GroupBy(x => x.Status, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.Sum(g => g.Count), StringComparer.Ordinal);
        var effective = groups.GroupBy(x => x.Effective, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.Sum(g => g.Count), StringComparer.Ordinal);
        return new SubscriptionReportView(generatedUtc, groups.Sum(x => x.Count),
            SubscriptionRules.Statuses.Select(x => new SubscriptionReportCount(x, counts.GetValueOrDefault(x))).ToArray(),
            new[] { "active", "scheduled", "expired", "cancelled", "suspended" }.Select(x => new SubscriptionReportCount(x, effective.GetValueOrDefault(x))).ToArray());
    }

    public async Task<SubscriptionEventReportView> GetEventsAsync(SubscriptionEventInterval interval, CancellationToken token)
    {
        if (!SubscriptionEventReportRules.ValidInterval(interval))
            throw new ArgumentException("The event interval must contain between one and 366 UTC days.", nameof(interval));
        token.ThrowIfCancellationRequested();
        var generatedUtc = clock.GetUtcNow().ToUniversalTime();
        var fromUtc = new DateTimeOffset(interval.From.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
        var toUtc = new DateTimeOffset(interval.To.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
        var groups = await database.Database.SqlQuery<EventGroup>($"""
            SELECT recorded."DayUtc", recorded."Key", COUNT(*) AS "Count"
            FROM (
                SELECT date_trunc('day', "CreatedUtc" AT TIME ZONE 'UTC') AT TIME ZONE 'UTC' AS "DayUtc",
                    CASE
                        WHEN "Action" = 'subscription.activated'
                            AND "BeforeStatus" IS NULL AND "AfterStatus" = 'active' THEN 'activated'
                        WHEN "Action" = 'subscription.reactivated'
                            AND "BeforeStatus" = 'cancelled' AND "AfterStatus" = 'active' THEN 'reactivated'
                        WHEN "Action" = 'subscription.cancelled'
                            AND "BeforeStatus" = 'active' AND "AfterStatus" = 'cancelled' THEN 'cancelled'
                        WHEN "Action" = 'subscription.updated'
                            AND "BeforeStatus" IN ('active', 'cancelled', 'suspended')
                            AND "AfterStatus" IN ('active', 'cancelled', 'suspended')
                            AND "BeforeStatus" <> "AfterStatus"
                            THEN 'updated_' || "BeforeStatus" || '_' || "AfterStatus"
                        WHEN "Action" = 'subscription.recovery_suspended'
                            AND "BeforeStatus" = 'active' AND "AfterStatus" = 'suspended' THEN 'recovery_suspended'
                        WHEN "Action" = 'subscription.assigned' AND "AfterStatus" = 'active'
                            AND ("BeforeStatus" IS NULL OR "BeforeStatus" IN ('active','cancelled','suspended'))
                            AND "AfterPlan" IN ('free_beta','probationismo','annual') AND "AfterStartsUtc" IS NOT NULL THEN 'assigned'
                        WHEN "Action" = 'subscription.renewed' AND "AfterStatus" = 'active'
                            AND "BeforePlan" = "AfterPlan" AND "AfterPlan" IN ('probationismo','annual')
                            AND "BeforeStartsUtc" = "AfterStartsUtc" AND "BeforeExpiresUtc" < "AfterExpiresUtc" THEN 'renewed'
                        ELSE 'unclassified'
                    END AS "Key"
                FROM subscriptions."Audit"
                WHERE "CreatedUtc" >= {fromUtc} AND "CreatedUtc" < {toUtc}
            ) AS recorded
            GROUP BY recorded."DayUtc", recorded."Key"
            ORDER BY recorded."DayUtc", recorded."Key"
            """).ToArrayAsync(token);
        token.ThrowIfCancellationRequested();
        var dailyCounts = groups.ToDictionary(
            x => (DateOnly.FromDateTime(x.DayUtc.UtcDateTime), x.Key), x => x.Count);
        var totals = groups.GroupBy(x => x.Key, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.Sum(group => group.Count), StringComparer.Ordinal);
        var days = new SubscriptionEventDay[interval.To.DayNumber - interval.From.DayNumber];
        for (var offset = 0; offset < days.Length; offset++)
        {
            token.ThrowIfCancellationRequested();
            var date = interval.From.AddDays(offset);
            var counts = SubscriptionEventReportRules.Keys.Select(key =>
                new SubscriptionEventCount(key, dailyCounts.GetValueOrDefault((date, key)))).ToArray();
            days[offset] = new SubscriptionEventDay(date, counts.Sum(x => x.Count), counts);
        }
        return new SubscriptionEventReportView(generatedUtc, fromUtc, toUtc, groups.Sum(x => x.Count),
            SubscriptionEventReportRules.Keys.Select(key => new SubscriptionEventCount(key, totals.GetValueOrDefault(key))).ToArray(), days);
    }

    private sealed class EventGroup
    {
        public DateTimeOffset DayUtc { get; set; }
        public string Key { get; set; } = "";
        public long Count { get; set; }
    }
}
