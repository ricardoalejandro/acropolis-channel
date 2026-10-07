using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Acropolis.Identity.Application;
using Acropolis.Identity.Infrastructure;
using Acropolis.Identity.IntegrationTests;
using Acropolis.Migrations;
using Acropolis.Subscriptions.Application;
using Acropolis.Subscriptions.Infrastructure;
using Acropolis.TestSupport;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Acropolis.Subscriptions.IntegrationTests;

[Collection("IdentityPostgres")]
public sealed class SubscriptionEventReportTests(IdentityFixture database)
{
    private const string Password = "Acropolis recorded subscription events phrase";
    private const string PrivateMarker = "qa-private-audit-reason@example.test";
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly DateOnly First = new(2026, 10, 1);

    [Fact]
    public async Task ExistingStockWithoutHistoryDoesNotInventEventsAndAll366DaysRemainPresent()
    {
        await database.ResetAsync(Token);
        await using (var seed = Context())
        {
            seed.Subscriptions.Add(Stock(Midnight(First)));
            await seed.SaveChangesAsync(Token);
        }
        var interval = new SubscriptionEventInterval(new DateOnly(2024, 1, 1), new DateOnly(2025, 1, 1));
        var clock = new FixedClock(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.FromHours(5)));
        await using var context = Context();
        var service = new SubscriptionReportService(context, clock);
        var report = await service.GetEventsAsync(interval, Token);
        Assert.Equal("recorded_events", report.Scope);
        Assert.Equal(clock.GetUtcNow().ToUniversalTime(), report.GeneratedUtc);
        Assert.Equal(TimeSpan.Zero, report.GeneratedUtc.Offset);
        Assert.Equal(Midnight(interval.From), report.FromUtc);
        Assert.Equal(Midnight(interval.To), report.ToUtc);
        Assert.Equal(0L, report.TotalEvents);
        Assert.Equal(366, report.Days.Length);
        Assert.All(report.ByEvent, bucket => Assert.Equal(0L, bucket.Count));
        Assert.Equal(SubscriptionEventReportRules.Keys, report.ByEvent.Select(bucket => bucket.Key));
        for (var index = 0; index < report.Days.Length; index++)
        {
            Assert.Equal(interval.From.AddDays(index), report.Days[index].DayUtc);
            Assert.Equal(0L, report.Days[index].TotalEvents);
            Assert.Equal(SubscriptionEventReportRules.Keys, report.Days[index].ByEvent.Select(bucket => bucket.Key));
            Assert.All(report.Days[index].ByEvent, bucket => Assert.Equal(0L, bucket.Count));
        }
        Assert.Empty(context.ChangeTracker.Entries());
        Assert.Equal(1L, (await service.GetCurrentAsync(Token)).Total);
        Assert.Equal(0, await context.Audit.CountAsync(Token));
    }

    [Fact]
    public async Task ExactUtcBoundariesIgnoreSessionTimeZoneAndAggregationDoesNotReadPersonalColumnsOrWrite()
    {
        await database.ResetAsync(Token);
        var from = Midnight(First);
        var to = Midnight(First.AddDays(2));
        var stock = Stock(from);
        await using (var seed = Context())
        {
            seed.Subscriptions.Add(stock);
            foreach (var at in new[] { from.AddTicks(-10), from, from.AddDays(1).AddTicks(-10), from.AddDays(1), to, to.AddTicks(10) })
                seed.Audit.Add(Event(stock, "subscription.activated", null, "active", at));
            await seed.SaveChangesAsync(Token);
        }
        string[] before;
        await using (var inspect = Context()) before = await AuditVersions(inspect);
        var projection = new EventProjection();
        await using var context = Context(interceptor: projection, timeZone: "Pacific/Honolulu");
        var report = await new SubscriptionReportService(context, new FixedClock(to.AddHours(1))).GetEventsAsync(new(First, First.AddDays(2)), Token);
        Assert.Equal(3L, report.TotalEvents);
        Assert.Equal(new long[] { 2, 1 }, report.Days.Select(day => day.TotalEvents));
        Assert.Equal(new[] { First, First.AddDays(1) }, report.Days.Select(day => day.DayUtc));
        Assert.Equal(3L, Count(report, "activated"));
        Assert.Equal(report.TotalEvents, report.ByEvent.Sum(bucket => bucket.Count));
        Assert.Equal(report.TotalEvents, report.Days.Sum(day => day.TotalEvents));
        Assert.Equal(1, projection.Aggregates);
        Assert.Empty(context.ChangeTracker.Entries());
        Assert.Equal(3, context.Database.GetCommandTimeout());
        await using var after = Context();
        Assert.Equal(before, await AuditVersions(after));
        Assert.Equal(6, await after.Audit.CountAsync(Token));
    }

    [Fact]
    public async Task EveryKnownTransitionHasItsBucketAndMalformedHistoryIsUnclassifiedWithoutLeakingValues()
    {
        await database.ResetAsync(Token);
        var at = Midnight(First).AddHours(12);
        var stock = Stock(at);
        var tuples = new (string Action, string? Before, string After)[]
        {
            ("subscription.activated", null, "active"),
            ("subscription.reactivated", "cancelled", "active"),
            ("subscription.cancelled", "active", "cancelled"),
            ("subscription.updated", "active", "cancelled"),
            ("subscription.updated", "active", "suspended"),
            ("subscription.updated", "cancelled", "active"),
            ("subscription.updated", "cancelled", "suspended"),
            ("subscription.updated", "suspended", "active"),
            ("subscription.updated", "suspended", "cancelled"),
            ("subscription.recovery_suspended", "active", "suspended"),
            ("unrecognized-private-action", "active", "cancelled"),
            ("subscription.updated", "active", "active"),
            ("subscription.activated", "cancelled", "active"),
            ("subscription.recovery_suspended", "suspended", "suspended"),
            ("subscription.activated", null, "suspended"),
            ("subscription.cancelled", null, "cancelled"),
            ("subscription.reactivated", "suspended", "active")
        };
        await using (var seed = Context())
        {
            seed.Subscriptions.Add(stock);
            foreach (var tuple in tuples) seed.Audit.Add(Event(stock, tuple.Action, tuple.Before, tuple.After, at));
            await seed.SaveChangesAsync(Token);
        }
        await using var context = Context();
        var report = await new SubscriptionReportService(context, new FixedClock(at)).GetEventsAsync(new(First, First.AddDays(1)), Token);
        Assert.Equal(17L, report.TotalEvents);
        Assert.Equal(SubscriptionEventReportRules.Keys, report.ByEvent.Select(bucket => bucket.Key));
        Assert.All(report.ByEvent.Where(bucket => bucket.Key != "unclassified"), bucket => Assert.Equal(1L, bucket.Count));
        Assert.Equal(7L, Count(report, "unclassified"));
        var day = Assert.Single(report.Days);
        Assert.Equal(First, day.DayUtc);
        Assert.Equal(report.TotalEvents, day.TotalEvents);
        Assert.Equal(report.ByEvent, day.ByEvent);
        Assert.Equal(1, await context.Subscriptions.CountAsync(Token));
        var json = JsonSerializer.Serialize(report);
        foreach (var value in new[] { PrivateMarker, stock.Id.ToString(), stock.UserId.ToString(), "unrecognized-private-action", "BeforeStatus", "AfterStatus", "ActorId", "Reason", "UserId", "SubscriptionId" })
            Assert.DoesNotContain(value, json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RealLifecycleCountsRecordedChangesAndIgnoresIdempotentNoOpsConflictsAndRepeatedRecovery()
    {
        await database.ResetAsync(Token);
        await using var api = new IdentityApiFactory(database);
        var member = await Create(api, "event-lifecycle-member@example.test");
        var manager = await Create(api, "event-lifecycle-manager@example.test", manage: true);
        var interval = Interval(api.Clock.GetUtcNow(), 2);
        using (var scope = api.Services.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ISubscriptionService>();
            var first = (await service.ActivateAsync(member.Id, Token)).Value!;
            Assert.True((await service.ActivateAsync(member.Id, Token)).Succeeded);
            Assert.Equal("concurrency_conflict", (await service.CancelAsync(member.Id, new("stale"), Token)).Error);
            var cancelled = (await service.CancelAsync(member.Id, new(first.Version), Token)).Value!;
            Assert.Equal(cancelled.Version, (await service.CancelAsync(member.Id, new(cancelled.Version), Token)).Value!.Version);
            var row = (await service.ActivateAsync(member.Id, Token)).Value!;
            Assert.Equal("concurrency_conflict", (await service.UpdateAsync(manager.Id, row.Id, new("stale", "suspended", "Synthetic stale mutation"), Token)).Error);
            Assert.Equal(row.Version, (await service.UpdateAsync(manager.Id, row.Id, new(row.Version, "active", "Synthetic no-op"), Token)).Value!.Version);
            foreach (var status in new[] { "cancelled", "suspended", "active", "suspended", "cancelled", "active" })
            {
                api.Clock.Advance(TimeSpan.FromSeconds(1));
                var changed = await service.UpdateAsync(manager.Id, row.Id, new(row.Version, status, "Synthetic explicit transition"), Token);
                Assert.True(changed.Succeeded);
                row = changed.Value!;
                Assert.Equal(status, row.Status);
            }
        }
        await using (var recovery = Context(migration: true))
        {
            var operations = new SubscriptionOperations(recovery, api.Clock);
            await operations.InvalidateRecoveryAsync(Token);
            await operations.InvalidateRecoveryAsync(Token);
        }
        await using var context = Context();
        var report = await new SubscriptionReportService(context, api.Clock).GetEventsAsync(interval, Token);
        Assert.Equal(10L, report.TotalEvents);
        Assert.All(report.ByEvent.Where(bucket => bucket.Key != "unclassified"), bucket => Assert.Equal(1L, bucket.Count));
        Assert.Equal(0L, Count(report, "unclassified"));
        Assert.Equal(10, await context.Audit.CountAsync(Token));
        Assert.Equal("suspended", (await context.Subscriptions.SingleAsync(Token)).Status);
    }

    [Fact]
    public async Task ConcurrentActivationAndCancellationRecordExactlyTheWinningEvents()
    {
        await database.ResetAsync(Token);
        await using var api = new IdentityApiFactory(database);
        var member = await Create(api, "event-concurrency@example.test");
        var interval = Interval(api.Clock.GetUtcNow());
        async Task<SubscriptionResult<SubscriptionView>> Activate()
        {
            using var scope = api.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IdentityDbContext>().Database.OpenConnectionAsync(Token);
            return await scope.ServiceProvider.GetRequiredService<ISubscriptionService>().ActivateAsync(member.Id, Token);
        }
        var activated = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Activate()));
        Assert.All(activated, result => Assert.True(result.Succeeded));
        Assert.Single(activated.Select(result => result.Value!.Id).Distinct());
        var row = activated[0].Value!;
        async Task<SubscriptionResult<SubscriptionView>> Cancel()
        {
            using var scope = api.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IdentityDbContext>().Database.OpenConnectionAsync(Token);
            return await scope.ServiceProvider.GetRequiredService<ISubscriptionService>().CancelAsync(member.Id, new(row.Version), Token);
        }
        var cancelled = await Task.WhenAll(Cancel(), Cancel());
        Assert.Single(cancelled, result => result.Succeeded);
        Assert.Equal("concurrency_conflict", cancelled.Single(result => !result.Succeeded).Error);
        await using var context = Context();
        var report = await new SubscriptionReportService(context, api.Clock).GetEventsAsync(interval, Token);
        Assert.Equal(2L, report.TotalEvents);
        Assert.Equal(1L, Count(report, "activated"));
        Assert.Equal(1L, Count(report, "cancelled"));
        Assert.All(report.ByEvent.Where(bucket => bucket.Key is not ("activated" or "cancelled")), bucket => Assert.Equal(0L, bucket.Count));
    }

    [Fact]
    public async Task FailureAfterPersistenceRollsBackBothStateAndAuditBeforeAnExplicitRetry()
    {
        await database.ResetAsync(Token);
        await using var api = new IdentityApiFactory(database);
        var member = await Create(api, "event-rollback@example.test");
        var interval = Interval(api.Clock.GetUtcNow());
        using (var scope = api.Services.CreateScope())
        {
            await using var failing = Context(interceptor: new FailAfterPersistence());
            var service = new SubscriptionService(failing, scope.ServiceProvider.GetRequiredService<IIdentityService>(), api.Clock);
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.ActivateAsync(member.Id, Token));
        }
        await using (var context = Context())
        {
            Assert.Equal(0, await context.Subscriptions.CountAsync(Token));
            Assert.Equal(0, await context.Audit.CountAsync(Token));
            Assert.Equal(0L, (await new SubscriptionReportService(context, api.Clock).GetEventsAsync(interval, Token)).TotalEvents);
        }
        using (var scope = api.Services.CreateScope()) Assert.True((await scope.ServiceProvider.GetRequiredService<ISubscriptionService>().ActivateAsync(member.Id, Token)).Succeeded);
        await using var final = Context();
        var report = await new SubscriptionReportService(final, api.Clock).GetEventsAsync(interval, Token);
        Assert.Equal(1L, report.TotalEvents);
        Assert.Equal(1L, Count(report, "activated"));
    }

    [Fact]
    public async Task UncommittedHistoryIsInvisibleAndRollbackDoesNotChangeTheAggregate()
    {
        await database.ResetAsync(Token);
        var at = Midnight(First).AddHours(12);
        var stock = Stock(at);
        await using (var seed = Context())
        {
            seed.Subscriptions.Add(stock);
            seed.Audit.Add(Event(stock, "subscription.activated", null, "active", at));
            await seed.SaveChangesAsync(Token);
        }
        await using (var pending = Context())
        {
            await using var transaction = await pending.Database.BeginTransactionAsync(Token);
            pending.Audit.Add(Event(stock, "subscription.updated", "active", "suspended", at));
            await pending.SaveChangesAsync(Token);
            await using var reader = Context();
            Assert.Equal(1L, (await new SubscriptionReportService(reader, new FixedClock(at)).GetEventsAsync(new(First, First.AddDays(1)), Token)).TotalEvents);
            await transaction.RollbackAsync(Token);
        }
        await using var final = Context();
        var report = await new SubscriptionReportService(final, new FixedClock(at)).GetEventsAsync(new(First, First.AddDays(1)), Token);
        Assert.Equal(1L, report.TotalEvents);
        Assert.Equal(0L, Count(report, "updated_active_suspended"));
        Assert.Equal(1, await final.Audit.CountAsync(Token));
    }

    [Fact]
    public async Task CancellationAndInvalidIntervalsDoNotWriteAndDoNotPreventAnExplicitFreshRead()
    {
        await database.ResetAsync(Token);
        await using var context = Context();
        var service = new SubscriptionReportService(context, new FixedClock(Midnight(First)));
        foreach (var invalid in new SubscriptionEventInterval?[] { null, new(First, First), new(First, First.AddDays(-1)), new(First, First.AddDays(367)) })
            await Assert.ThrowsAnyAsync<ArgumentException>(() => service.GetEventsAsync(invalid!, Token));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GetEventsAsync(new(First, First.AddDays(1)), cancellation.Token));
        var fresh = await service.GetEventsAsync(new(First, First.AddDays(1)), Token);
        Assert.Equal(0L, fresh.TotalEvents);
        Assert.Equal(0, await context.Audit.CountAsync(Token));
        Assert.Equal(0, await context.Subscriptions.CountAsync(Token));
        Assert.Equal(3, context.Database.GetCommandTimeout());
    }

    [Fact]
    public async Task RealMfaHttpReadsCommittedEventsAndLeavesTheCurrentSnapshotIndependent()
    {
        await database.ResetAsync(Token);
        await using var api = new IdentityApiFactory(database);
        var member = await Create(api, "event-http-member@example.test");
        var manager = await Create(api, "event-http-manager@example.test", manage: true);
        var interval = Interval(api.Clock.GetUtcNow(), 2);
        using (var scope = api.Services.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ISubscriptionService>();
            var row = (await service.ActivateAsync(member.Id, Token)).Value!;
            Assert.True((await service.CancelAsync(member.Id, new(row.Version), Token)).Succeeded);
            Assert.True((await service.ActivateAsync(member.Id, Token)).Succeeded);
        }
        using var client = api.Client();
        using var login = await MfaTestClient.Login(client, manager.Email!, Password, Token);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var from = interval.From.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var to = interval.To.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        using var response = await client.GetAsync($"/api/v1/admin/reports/subscriptions/events?from={from}&to={to}", Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore == true);
        var report = (await response.Content.ReadFromJsonAsync<SubscriptionEventReportView>(Token))!;
        Assert.Equal("recorded_events", report.Scope);
        Assert.Equal(3L, report.TotalEvents);
        Assert.Equal(1L, Count(report, "activated"));
        Assert.Equal(1L, Count(report, "cancelled"));
        Assert.Equal(1L, Count(report, "reactivated"));
        Assert.Equal(Midnight(interval.From), report.FromUtc);
        Assert.Equal(Midnight(interval.To), report.ToUtc);
        using var current = await client.GetAsync("/api/v1/admin/reports/subscriptions", Token);
        Assert.Equal(HttpStatusCode.OK, current.StatusCode);
        var stock = (await current.Content.ReadFromJsonAsync<SubscriptionReportView>(Token))!;
        Assert.Equal("current", stock.Scope);
        Assert.Equal(1L, stock.Total);
        Assert.Equal(1L, stock.ByStatus.Single(bucket => bucket.Key == "active").Count);
    }

    private SubscriptionsDbContext Context(bool migration = false, IInterceptor? interceptor = null, string? timeZone = null)
    {
        var settings = new NpgsqlConnectionStringBuilder(migration ? database.MigrationConnection : database.RuntimeConnection);
        if (timeZone is not null) settings.Options += " -c TimeZone=" + timeZone;
        var options = new DbContextOptionsBuilder<SubscriptionsDbContext>();
        SubscriptionsRegistration.ConfigureDatabase(options, settings.ConnectionString);
        if (interceptor is not null) options.AddInterceptors(interceptor);
        return new(options.Options);
    }
    private static DateTimeOffset Midnight(DateOnly day) => new(day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
    private static SubscriptionEventInterval Interval(DateTimeOffset now, int days = 1)
    {
        var day = DateOnly.FromDateTime(now.UtcDateTime);
        return new(day, day.AddDays(days));
    }
    private static long Count(SubscriptionEventReportView report, string key) => report.ByEvent.Single(bucket => bucket.Key == key).Count;
    private static Subscription Stock(DateTimeOffset at) => new() { Id = Guid.NewGuid(), UserId = Guid.NewGuid(), CreatedUtc = at, ActivatedUtc = at, UpdatedUtc = at };
    private static SubscriptionAudit Event(Subscription stock, string action, string? before, string after, DateTimeOffset at) => new()
    {
        Id = Guid.NewGuid(),
        SubscriptionId = stock.Id,
        UserId = stock.UserId,
        ActorId = Guid.NewGuid(),
        Action = action,
        BeforeStatus = before,
        AfterStatus = after,
        Reason = PrivateMarker,
        CreatedUtc = at
    };
    private static Task<string[]> AuditVersions(SubscriptionsDbContext context) => context.Database.SqlQueryRaw<string>("SELECT xmin::text AS \"Value\" FROM subscriptions.\"Audit\" ORDER BY \"Id\"").ToArrayAsync(Token);
    private static async Task<ChannelUser> Create(IdentityApiFactory api, string email, bool manage = false)
    {
        using var scope = api.Services.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<UserManager<ChannelUser>>();
        var user = new ChannelUser { Id = Guid.NewGuid(), UserName = email, Email = email, DisplayName = "QA Event Subscriber", EmailConfirmed = true, SubscriptionsManage = manage, LockoutEnabled = true };
        Assert.True((await manager.CreateAsync(user, Password)).Succeeded);
        Token.ThrowIfCancellationRequested();
        return user;
    }
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
    private sealed class FailAfterPersistence : SaveChangesInterceptor
    {
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Synthetic failure after persistence before transaction commit.");
    }
    private sealed class EventProjection : DbCommandInterceptor
    {
        public int Aggregates { get; private set; }
        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("GROUP BY", StringComparison.OrdinalIgnoreCase))
            {
                Aggregates++;
                foreach (var field in new[] { "UserId", "ActorId", "SubscriptionId", "Reason" }) Assert.DoesNotContain('"' + field + '"', command.CommandText, StringComparison.Ordinal);
                Assert.DoesNotContain("JOIN", command.CommandText, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("identity.", command.CommandText, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("subscriptions.\"Audit\"", command.CommandText, StringComparison.Ordinal);
            }
            return ValueTask.FromResult(result);
        }
    }
}
