using System.Data.Common;
using Acropolis.Identity.IntegrationTests;
using Acropolis.Subscriptions.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Acropolis.Subscriptions.IntegrationTests;

[Collection("IdentityPostgres")]
public sealed class OperationalReportTests(IdentityFixture database)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task EmptySubscriptionSnapshotIncludesEveryStateWithoutWriting()
    {
        await database.ResetAsync(Token);
        await using var context = Context();
        var clock = new TestClock();
        var report = await new SubscriptionReportService(context, clock).GetCurrentAsync(Token);
        Assert.Equal(clock.GetUtcNow(), report.GeneratedUtc);
        Assert.Equal(TimeSpan.Zero, report.GeneratedUtc.Offset);
        Assert.Equal("current", report.Scope);
        Assert.Equal(0L, report.Total);
        Assert.Equal(new[] { "active", "cancelled", "suspended" }, report.ByStatus.Select(x => x.Key));
        Assert.All(report.ByStatus, x => Assert.Equal(0L, x.Count));
        Assert.Empty(context.ChangeTracker.Entries());
        Assert.Equal(0, await context.Audit.CountAsync(Token));
    }

    [Fact]
    public async Task SubscriptionStatesCountActualRowsIndependentlyOfAccountsAndDoNotProjectPersonalData()
    {
        await database.ResetAsync(Token);
        var now = new TestClock().GetUtcNow();
        await using (var seed = Context())
        {
            foreach (var state in new[] { "active", "active", "cancelled", "suspended" })
                seed.Subscriptions.Add(new Subscription
                {
                    Id = Guid.NewGuid(),
                    UserId = Guid.NewGuid(),
                    Status = state,
                    CreatedUtc = now.AddYears(-1),
                    ActivatedUtc = now.AddMonths(-6),
                    UpdatedUtc = now,
                    CancelledUtc = state == "cancelled" ? now : null
                });
            await seed.SaveChangesAsync(Token);
        }
        var projection = new ReportProjection();
        await using var context = Context(projection);
        var before = await context.Database.SqlQueryRaw<string>("SELECT xmin::text AS \"Value\" FROM subscriptions.\"Subscriptions\" ORDER BY \"Id\"").ToArrayAsync(Token);
        var report = await new SubscriptionReportService(context, new TestClock()).GetCurrentAsync(Token);
        Assert.Equal(4L, report.Total);
        Assert.Equal(new long[] { 2, 1, 1 }, report.ByStatus.Select(x => x.Count));
        Assert.Equal(report.Total, report.ByStatus.Sum(x => x.Count));
        Assert.Equal(1, projection.Aggregates);
        Assert.Empty(context.ChangeTracker.Entries());
        Assert.Equal(before, await context.Database.SqlQueryRaw<string>("SELECT xmin::text AS \"Value\" FROM subscriptions.\"Subscriptions\" ORDER BY \"Id\"").ToArrayAsync(Token));
        Assert.Equal(0, await context.Audit.CountAsync(Token));
        Assert.Equal(3, context.Database.GetCommandTimeout());
    }

    [Fact]
    public async Task CancellationDoesNotWriteAndDoesNotPreventAnExplicitRetry()
    {
        await database.ResetAsync(Token);
        await using var context = Context();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var service = new SubscriptionReportService(context, new TestClock());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GetCurrentAsync(cancelled.Token));
        Assert.Equal(0L, (await service.GetCurrentAsync(Token)).Total);
        Assert.Equal(0, await context.Audit.CountAsync(Token));
    }

    private SubscriptionsDbContext Context(IInterceptor? interceptor = null)
    {
        var options = new DbContextOptionsBuilder<SubscriptionsDbContext>();
        SubscriptionsRegistration.ConfigureDatabase(options, database.RuntimeConnection);
        if (interceptor is not null) options.AddInterceptors(interceptor);
        return new SubscriptionsDbContext(options.Options);
    }
    private sealed class ReportProjection : DbCommandInterceptor
    {
        public int Aggregates { get; private set; }
        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("GROUP BY", StringComparison.Ordinal))
            {
                Aggregates++;
                foreach (var field in new[] { "UserId", "Id", "Plan", "Version", "CreatedUtc", "ActivatedUtc" })
                    Assert.DoesNotContain("\"" + field + "\"", command.CommandText, StringComparison.Ordinal);
                Assert.DoesNotContain("JOIN", command.CommandText, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("identity.", command.CommandText, StringComparison.OrdinalIgnoreCase);
            }
            return ValueTask.FromResult(result);
        }
    }
}
