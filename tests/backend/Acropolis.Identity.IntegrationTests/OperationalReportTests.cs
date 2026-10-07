using System.Data.Common;
using Acropolis.Identity.Application;
using Acropolis.Identity.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Acropolis.Identity.IntegrationTests;

[Collection("IdentityPostgres")]
public sealed class OperationalReportTests(IdentityFixture database)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task EmptySnapshotIncludesEveryStatusAndInstitutionalLevelWithoutWriting()
    {
        await database.ResetAsync(Token);
        await using var context = database.Context();
        var clock = new TestClock();
        var report = await new IdentityReportService(context, clock).GetCurrentAsync(Token);
        Assert.Equal(clock.GetUtcNow(), report.GeneratedUtc);
        Assert.Equal(TimeSpan.Zero, report.GeneratedUtc.Offset);
        Assert.Equal("current", report.Scope);
        Assert.Equal(0L, report.Total);
        Assert.Equal(new[] { "active", "pending", "disabled" }, report.ByStatus.Select(x => x.Key));
        Assert.Equal(IdentityRules.Levels, report.ByLevel.Select(x => x.Key));
        Assert.All(report.ByStatus, group => Assert.Equal(0L, group.Count));
        Assert.All(report.ByLevel, group => Assert.Equal(0L, group.Count));
        Assert.Empty(context.ChangeTracker.Entries());
        Assert.Equal(0, await context.Audit.CountAsync(Token));
        Assert.Null(context.Database.CurrentTransaction);
        Assert.Equal(3, context.Database.GetCommandTimeout());
    }

    [Fact]
    public async Task AllAccountStateFlagsAreCountedOnceWhileLevelsCoexistAndMfaDoesNotChangeStatus()
    {
        await database.ResetAsync(Token);
        await using (var seed = database.Context())
        {
            var index = 0;
            foreach (var confirmed in new[] { false, true })
                foreach (var disabled in new[] { false, true })
                    foreach (var revalidation in new[] { false, true })
                        foreach (var mfa in new[] { false, true })
                        {
                            var user = User("flags-" + index++);
                            user.EmailConfirmed = confirmed;
                            user.IsDisabled = disabled;
                            user.RevalidationRequired = revalidation;
                            user.TwoFactorEnabled = mfa;
                            IReadOnlyList<string> levels = index == 1 ? IdentityRules.Levels : ["Externo"];
                            user.Levels = levels.Select(level => new UserLevel { UserId = user.Id, Level = level }).ToList();
                            seed.Users.Add(user);
                        }
            await seed.SaveChangesAsync(Token);
        }
        await using var context = database.Context();
        var before = await context.Database.SqlQueryRaw<string>("SELECT xmin::text AS \"Value\" FROM identity.\"Users\" ORDER BY \"Id\"").ToArrayAsync(Token);
        var report = await new IdentityReportService(context, new TestClock()).GetCurrentAsync(Token);
        Assert.Equal(16L, report.Total);
        Assert.Equal(new long[] { 2, 2, 12 }, report.ByStatus.Select(x => x.Count));
        Assert.Equal(report.Total, report.ByStatus.Sum(x => x.Count));
        Assert.Equal(new long[] { 16, 1, 1, 1, 1, 1 }, report.ByLevel.Select(x => x.Count));
        Assert.True(report.ByLevel.Sum(x => x.Count) > report.Total);
        Assert.Empty(context.ChangeTracker.Entries());
        Assert.Equal(before, await context.Database.SqlQueryRaw<string>("SELECT xmin::text AS \"Value\" FROM identity.\"Users\" ORDER BY \"Id\"").ToArrayAsync(Token));
        Assert.Equal(0, await context.Audit.CountAsync(Token));
    }

    [Fact]
    public async Task ConcurrentAccountAndLevelInsertionCannotSplitTheReportSnapshot()
    {
        await database.ResetAsync(Token);
        await using (var seed = database.Context())
        {
            var first = User("snapshot-first");
            first.Levels.Add(new UserLevel { UserId = first.Id, Level = "Externo" });
            seed.Users.Add(first);
            await seed.SaveChangesAsync(Token);
        }
        var mutation = new AfterAccountAggregate(async token =>
        {
            await using var writer = database.Context();
            var second = User("snapshot-second");
            second.Levels.Add(new UserLevel { UserId = second.Id, Level = "Externo" });
            writer.Users.Add(second);
            await writer.SaveChangesAsync(token);
        });
        await using var context = Context(mutation);
        var service = new IdentityReportService(context, new TestClock());
        var original = await service.GetCurrentAsync(Token);
        Assert.Equal(1, mutation.Calls);
        Assert.Equal(1L, original.Total);
        Assert.Equal(1L, original.ByStatus.Single(x => x.Key == "active").Count);
        Assert.Equal(1L, original.ByLevel.Single(x => x.Key == "Externo").Count);
        var current = await service.GetCurrentAsync(Token);
        Assert.Equal(2L, current.Total);
        Assert.Equal(2L, current.ByLevel.Single(x => x.Key == "Externo").Count);
        Assert.Null(context.Database.CurrentTransaction);
        Assert.Empty(context.ChangeTracker.Entries());
    }

    [Fact]
    public async Task CancellationAfterTheFirstAggregateDisposesTheSnapshotAndAllowsAnExplicitRetry()
    {
        await database.ResetAsync(Token);
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var cancellation = new AfterAccountAggregate(_ => { cancelled.Cancel(); return Task.CompletedTask; });
        await using var context = Context(cancellation);
        var service = new IdentityReportService(context, new TestClock());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GetCurrentAsync(cancelled.Token));
        Assert.Equal(1, cancellation.Calls);
        Assert.Null(context.Database.CurrentTransaction);
        Assert.Equal(3, context.Database.GetCommandTimeout());
        var retry = await service.GetCurrentAsync(Token);
        Assert.Equal(0L, retry.Total);
        Assert.Equal(0, await context.Audit.CountAsync(Token));
    }

    private IdentityDbContext Context(IInterceptor interceptor)
    {
        var options = new DbContextOptionsBuilder<IdentityDbContext>();
        IdentityRegistration.ConfigureDatabase(options, database.RuntimeConnection);
        options.AddInterceptors(interceptor);
        return new IdentityDbContext(options.Options);
    }
    private static ChannelUser User(string suffix)
    {
        var email = suffix + "@example.test";
        return new ChannelUser
        {
            Id = Guid.NewGuid(), UserName = email, NormalizedUserName = email.ToUpperInvariant(),
            Email = email, NormalizedEmail = email.ToUpperInvariant(), EmailConfirmed = true,
            DisplayName = "Synthetic report account", SecurityStamp = Guid.NewGuid().ToString("N")
        };
    }
    private sealed class AfterAccountAggregate(Func<CancellationToken, Task> action) : DbCommandInterceptor
    {
        public int Calls { get; private set; }
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (Calls == 0 && command.CommandText.Contains("GROUP BY", StringComparison.Ordinal)
                && command.CommandText.Contains("\"Users\"", StringComparison.Ordinal))
            {
                Calls++;
                await action(cancellationToken);
            }
            return result;
        }
    }
}
