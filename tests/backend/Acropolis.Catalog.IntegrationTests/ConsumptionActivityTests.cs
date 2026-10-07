using System.Data.Common;
using System.Text.Json;
using Acropolis.Catalog.Application;
using Acropolis.Catalog.Infrastructure;
using Acropolis.Migrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Acropolis.Catalog.IntegrationTests;

[Collection("CatalogPostgres")]
public sealed class ConsumptionActivityTests(CatalogFixture fixture)
{
    private CancellationToken Token => TestContext.Current.CancellationToken;
    private const string Binding = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
    [Fact]
    public async Task MigrationAddsEmptyOwnedTablesWithCoherentModelAndReadonlyPulsePrivilege()
    {
        await fixture.ResetAsync(Token);
        await using var context = fixture.Context();
        Assert.False(context.Database.HasPendingModelChanges());
        Assert.Empty(await context.ConsumptionSessions.ToArrayAsync(Token));
        Assert.Empty(await context.ConsumptionPulses.ToArrayAsync(Token));
        Assert.Empty(await context.ConsumptionDaily.ToArrayAsync(Token));
        Assert.Empty(await context.ConsumptionAccountDaily.ToArrayAsync(Token));
        await new ChannelMigrationRunner().RunAsync(fixture.MigrationConnection, Token);
        Assert.Contains("20261007043000_AddConsumptionActivity", await context.Database.GetAppliedMigrationsAsync(Token));
        await using var connection = new NpgsqlConnection(fixture.RuntimeConnection); await connection.OpenAsync(Token);
        await using var command = new NpgsqlCommand("UPDATE catalog.\"ConsumptionPulses\" SET \"CreditedMs\"=1", connection);
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, (await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync(Token))).SqlState);
    }
    [Fact]
    public async Task DisabledRecordingAndPublicWorkLoadCreateNoSessionPulseOrAggregate()
    {
        await fixture.ResetAsync(Token);
        var clock = new ActivityClock(Now);
        await using var context = fixture.Context();
        var work = await Publish(context, clock, "consumo-disabled");
        var service = Recording(context, clock, false);
        Assert.False(service.GetCapabilities().RecordingEnabled);
        Assert.Equal(90, service.GetCapabilities().DetailRetentionDays); Assert.Equal(365, service.GetCapabilities().GeneralRetentionDays);
        Assert.Equal("recording_disabled", (await service.StartAsync(Guid.NewGuid(), Binding, work.Slug, new(Guid.NewGuid(), work.Version), Token)).Error);
        Assert.Equal("recording_disabled", (await service.PulseAsync(Guid.NewGuid(), Binding, Guid.NewGuid(), Reading(), Token)).Error);
        Assert.NotNull(await new CatalogService(context, clock).GetPublishedAsync(work.Slug, Token));
        Assert.Equal(work.Version, (await new CatalogService(context, clock).GetPublishedWorkAsync(work.Slug, Token))!.Version);
        Assert.Equal(0, await context.ConsumptionSessions.CountAsync(Token)); Assert.Equal(0, await context.ConsumptionPulses.CountAsync(Token)); Assert.Equal(0, await context.ConsumptionDaily.CountAsync(Token)); Assert.Equal(0, await context.ConsumptionAccountDaily.CountAsync(Token));
    }
    [Fact]
    public async Task ConcurrentDuplicateStartsReturnOneSessionAndIncrementDailyExactlyOnce()
    {
        await fixture.ResetAsync(Token);
        var clock = new ActivityClock(Now); AdminContentView work;
        await using (var setup = fixture.Context()) work = await Publish(setup, clock, "consumo-start-race");
        var account = Guid.NewGuid(); var request = new StartConsumptionRequest(Guid.NewGuid(), work.Version);
        async Task<CatalogResult<ConsumptionSessionView>> Start()
        {
            await using var context = fixture.Context(); return await Recording(context, clock).StartAsync(account, Binding, work.Slug, request, Token);
        }
        var results = await Task.WhenAll(Start(), Start());
        Assert.All(results, x => Assert.True(x.Succeeded)); Assert.Equal(results[0].Value, results[1].Value);
        await using var final = fixture.Context();
        Assert.Equal(1, await final.ConsumptionSessions.CountAsync(Token)); Assert.Equal(1, (await final.ConsumptionDaily.SingleAsync(Token)).Starts); Assert.Equal(1, (await final.ConsumptionAccountDaily.SingleAsync(Token)).Starts);
        Assert.Equal(2, await final.Audit.CountAsync(Token));
        var conflict = await Recording(final, clock).StartAsync(account, new string('b', 64), work.Slug, request, Token);
        Assert.Equal(409, conflict.Status); Assert.Equal("concurrency_conflict", conflict.Error);
        Assert.Null(final.Database.CurrentTransaction); Assert.Empty(final.ChangeTracker.Entries());
    }
    [Fact]
    public async Task ConcurrentPulseReplayReturnsStableReceiptAndBodyConflictDoesNotDoubleAggregate()
    {
        await fixture.ResetAsync(Token);
        var clock = new ActivityClock(Now); var account = Guid.NewGuid(); ConsumptionSessionView session;
        await using (var setup = fixture.Context())
        {
            var work = await Publish(setup, clock, "consumo-pulse-race");
            session = (await Recording(setup, clock).StartAsync(account, Binding, work.Slug, new(Guid.NewGuid(), work.Version), Token)).Value!;
        }
        clock.Advance(TimeSpan.FromSeconds(1));
        async Task<CatalogResult<ConsumptionPulseReceipt>> Pulse(ConsumptionPulseRequest request)
        {
            await using var context = fixture.Context(); var result = await Recording(context, clock).PulseAsync(account, Binding, session.SessionId, request, Token);
            Assert.Null(context.Database.CurrentTransaction); Assert.Empty(context.ChangeTracker.Entries()); return result;
        }
        var results = await Task.WhenAll(Pulse(Reading()), Pulse(Reading()));
        Assert.All(results, x => Assert.True(x.Succeeded)); Assert.Equal(results[0].Value, results[1].Value);
        Assert.Equal(1000, results[0].Value!.CreditedMs);
        Assert.Equal("concurrency_conflict", (await Pulse(Reading() with { ActiveMs = 999 })).Error);
        Assert.Equal("concurrency_conflict", (await Pulse(Reading() with { Sequence = 3 })).Error);
        await using var final = fixture.Context();
        Assert.Equal(1, await final.ConsumptionPulses.CountAsync(Token));
        var day = await final.ConsumptionDaily.SingleAsync(Token);
        Assert.Equal(1, day.Starts); Assert.Equal(1, day.RecordedPulses); Assert.Equal(1000, day.CreditedMs); Assert.Equal(5000, day.ProgressBasisPointsSum);
        var accountDay = await final.ConsumptionAccountDaily.SingleAsync(Token); Assert.Equal(1, accountDay.RecordedPulses); Assert.Equal(1000, accountDay.CreditedMs);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True((await Recording(final, clock).PulseAsync(account, Binding, session.SessionId, Reading() with { Sequence = 2 }, Token)).Succeeded);
        Assert.Equal(2, await final.ConsumptionPulses.CountAsync(Token));
    }
    [Fact]
    public async Task AccountBindingPublicationAndContentVersionAreRecheckedForEachPulse()
    {
        await fixture.ResetAsync(Token);
        var clock = new ActivityClock(Now); var account = Guid.NewGuid();
        await using var context = fixture.Context(); var work = await Publish(context, clock, "consumo-live-check"); var recorder = Recording(context, clock);
        var session = (await recorder.StartAsync(account, Binding, work.Slug, new(Guid.NewGuid(), work.Version), Token)).Value!;
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(404, (await recorder.PulseAsync(Guid.NewGuid(), Binding, session.SessionId, Reading(), Token)).Status);
        Assert.Equal(404, (await recorder.PulseAsync(account, new string('b', 64), session.SessionId, Reading(), Token)).Status); // Revalidated credentials cannot resume a restored old binding.
        var withdrawn = (await new CatalogService(context, clock).UpdateAsync(Guid.NewGuid(), work.Id, Change(work, "draft"), Token)).Value!;
        Assert.Equal(404, (await recorder.PulseAsync(account, Binding, session.SessionId, Reading(), Token)).Status);
        var published = (await new CatalogService(context, clock).UpdateAsync(Guid.NewGuid(), work.Id, Change(withdrawn, "published"), Token)).Value!;
        Assert.NotEqual(work.Version, published.Version);
        Assert.Equal("content_changed", (await recorder.PulseAsync(account, Binding, session.SessionId, Reading(), Token)).Error);
        Assert.Equal(0, await context.ConsumptionPulses.CountAsync(Token)); Assert.Equal(0, (await context.ConsumptionDaily.SingleAsync(Token)).RecordedPulses);
    }
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task FailureAfterRealDailyWriteRollsBackDetailAggregateAndKeepsScopedContextUsable(bool failStart, bool failAccountDaily)
    {
        await fixture.ResetAsync(Token);
        var clock = new ActivityClock(Now); var account = Guid.NewGuid(); var visit = Guid.NewGuid(); AdminContentView work; ConsumptionSessionView? session = null;
        await using (var setup = fixture.Context())
        {
            work = await Publish(setup, clock, "consumo-rollback");
            if (!failStart) session = (await Recording(setup, clock).StartAsync(account, Binding, work.Slug, new(Guid.NewGuid(), work.Version), Token)).Value!;
        }
        clock.Advance(TimeSpan.FromSeconds(1));
        await using var context = fixture.Context(interceptor: new FailAfterDailyWrite(failAccountDaily));
        var recorder = Recording(context, clock);
        if (failStart) await Assert.ThrowsAsync<InvalidOperationException>(() => recorder.StartAsync(account, Binding, work.Slug, new(visit, work.Version), Token));
        else await Assert.ThrowsAsync<InvalidOperationException>(() => recorder.PulseAsync(account, Binding, session!.SessionId, Reading(), Token));
        Assert.Null(context.Database.CurrentTransaction); Assert.Empty(context.ChangeTracker.Entries());
        Assert.Equal(failStart ? 0 : 1, await context.ConsumptionSessions.CountAsync(Token)); Assert.Equal(0, await context.ConsumptionPulses.CountAsync(Token));
        Assert.Equal(failStart ? 0 : 1, await context.ConsumptionDaily.CountAsync(Token)); Assert.Equal(failStart ? 0 : 1, await context.ConsumptionAccountDaily.CountAsync(Token));
        if (!failStart) { Assert.Equal(0, (await context.ConsumptionAccountDaily.SingleAsync(Token)).RecordedPulses); Assert.Equal(0, (await context.ConsumptionDaily.SingleAsync(Token)).RecordedPulses); Assert.Equal(0, (await context.ConsumptionSessions.SingleAsync(Token)).LastSequence); }
        Assert.Equal(2, await context.Audit.CountAsync(Token));
        // The interceptor fails once. An explicit retry uses the same scoped recorder/context.
        if (failStart) Assert.True((await recorder.StartAsync(account, Binding, work.Slug, new(visit, work.Version), Token)).Succeeded);
        else Assert.True((await recorder.PulseAsync(account, Binding, session!.SessionId, Reading(), Token)).Succeeded);
        Assert.Equal(1, await context.ConsumptionSessions.CountAsync(Token));
        Assert.Equal(failStart ? 0 : 1, await context.ConsumptionPulses.CountAsync(Token));
        Assert.Equal(1, (await context.ConsumptionDaily.SingleAsync(Token)).Starts);
        Assert.Equal(failStart ? 0 : 1, (await context.ConsumptionDaily.SingleAsync(Token)).RecordedPulses);
        Assert.Equal(failStart ? 0 : 1, (await context.ConsumptionAccountDaily.SingleAsync(Token)).RecordedPulses);
        Assert.Null(context.Database.CurrentTransaction); Assert.Equal(2, await context.Audit.CountAsync(Token));
    }
    [Fact]
    public async Task GeneralAndPerContentCountsReconcileWhileRecentDistinctAccountsAreNonAdditive()
    {
        await fixture.ResetAsync(Token);
        var clock = new ActivityClock(Now); var firstAccount = Guid.NewGuid(); var otherAccount = Guid.NewGuid();
        await using var context = fixture.Context(); var first = await Publish(context, clock, "consumo-report-a"); var second = await Publish(context, clock, "consumo-report-b");
        foreach (var pair in new[] { (firstAccount, first, 1000), (firstAccount, second, 7000), (otherAccount, first, 3000) })
        {
            var recorder = Recording(context, clock); var session = (await recorder.StartAsync(pair.Item1, Binding, pair.Item2.Slug, new(Guid.NewGuid(), pair.Item2.Version), Token)).Value!;
            clock.Advance(TimeSpan.FromSeconds(1));
            Assert.True((await recorder.PulseAsync(pair.Item1, Binding, session.SessionId, Reading(pair.Item3), Token)).Succeeded);
        }
        var reports = new ConsumptionActivityReportService(context, clock, Enabled()); var interval = new ConsumptionReportInterval(new(2026, 10, 7), new(2026, 10, 8));
        var report = await reports.GetAsync(interval, Token);
        Assert.Equal("recorded_consumption_general", report.Scope); Assert.Equal(3, report.Summary.Starts); Assert.Equal(3, report.Summary.RecordedPulses); Assert.Equal(2, report.Summary.AccountsWithActivity);
        Assert.Equal(3000, report.Summary.CreditedMs); Assert.Equal(11000, report.Summary.ProgressBasisPointsSum); Assert.Equal(3666, report.Summary.MeanProgressBasisPoints);
        Assert.Equal(report.Summary.Starts, report.ByCategory.Sum(x => x.Metrics.Starts)); Assert.Equal(report.Summary.RecordedPulses, report.ByKind.Sum(x => x.Metrics.RecordedPulses));
        Assert.Equal(report.Summary.CreditedMs, report.Daily.Sum(x => x.Metrics.CreditedMs)); Assert.Equal(report.Summary.ProgressBasisPointsSum, report.Daily.Sum(x => x.Metrics.ProgressBasisPointsSum));
        Assert.Equal(6, report.ByCategory.Length); Assert.Equal(2, report.ByKind.Length);
        var page = await reports.ListContentAsync(interval, 1, 1, Token); var next = await reports.ListContentAsync(interval, 2, 1, Token);
        Assert.Equal(2, page.Total); Assert.Equal(first.Id, Assert.Single(page.Items).ContentId); Assert.Equal(2, page.Items[0].Metrics.AccountsWithActivity);
        Assert.Equal(second.Id, Assert.Single(next.Items).ContentId); Assert.Equal(1, next.Items[0].Metrics.AccountsWithActivity);
        Assert.True(page.Items[0].Metrics.AccountsWithActivity + next.Items[0].Metrics.AccountsWithActivity > report.Summary.AccountsWithActivity);
        var account = await reports.GetAccountAsync(firstAccount, interval, 1, 20, Token);
        Assert.Equal("recorded_consumption_account", account.Scope); Assert.Equal(2, account.Summary.Starts); Assert.Equal(1, account.Summary.AccountsWithActivity); Assert.Equal(2, account.Total);
        Assert.Empty((await reports.ListContentAsync(interval, 3, 1, Token)).Items); Assert.Empty(context.ChangeTracker.Entries());
        var json = JsonSerializer.Serialize(new { report, page, account });
        foreach (var privateField in new[] { "AccountId", "AuthenticationBindingHash", "WorkText", "YouTubeId", "@example.test", firstAccount.ToString(), otherAccount.ToString() }) Assert.DoesNotContain(privateField, json);
    }
    [Fact]
    public async Task RetentionUsesNinetyUtcDatesAnd365GeneralDatesWithoutLosingOrRecreatingAggregates()
    {
        await fixture.ResetAsync(Token);
        var generalCutoff = ConsumptionActivityRules.GeneralAvailableFrom(Now);
        var detailCutoff = ConsumptionActivityRules.DetailAvailableFrom(Now);
        var clock = new ActivityClock(generalCutoff.AddDays(-1)); var account = Guid.NewGuid();
        await using var context = fixture.Context(); var work = await Publish(context, clock, "consumo-retention");
        var recorder = Recording(context, clock);
        foreach (var startTime in new[] { generalCutoff.AddDays(-1), detailCutoff.AddDays(-1), detailCutoff })
        {
            clock.Set(startTime);
            var session = (await recorder.StartAsync(account, Binding, work.Slug, new(Guid.NewGuid(), work.Version), Token)).Value!;
            clock.Advance(TimeSpan.FromSeconds(1)); Assert.True((await recorder.PulseAsync(account, Binding, session.SessionId, Reading(), Token)).Succeeded);
        }
        clock.Set(Now);
        var interval = new ConsumptionReportInterval(DateOnly.FromDateTime(generalCutoff.UtcDateTime), new(2026, 10, 8));
        var reports = new ConsumptionActivityReportService(context, clock, Enabled()); var before = await reports.GetAsync(interval, Token);
        Assert.Equal(2, before.Summary.Starts); Assert.Equal(2, before.Summary.RecordedPulses); Assert.Null(before.Summary.AccountsWithActivity);
        var first = await new ConsumptionRetentionService(context, clock).PruneAsync(Token);
        Assert.Equal(new ConsumptionPruneResult(3, 3, 2, 1), first);
        Assert.Empty(await context.ConsumptionSessions.AsNoTracking().ToArrayAsync(Token)); Assert.Empty(await context.ConsumptionPulses.AsNoTracking().ToArrayAsync(Token));
        Assert.Equal(DateOnly.FromDateTime(detailCutoff.UtcDateTime), (await context.ConsumptionAccountDaily.AsNoTracking().SingleAsync(Token)).DayUtc);
        Assert.Equal(before.Summary, (await reports.GetAsync(interval, Token)).Summary);
        Assert.Equal(new ConsumptionPruneResult(0, 0, 0, 0), await new ConsumptionRetentionService(context, clock).PruneAsync(Token)); // Restart/repeat doesn't archive twice.
        var accountReport = await reports.GetAccountAsync(account, interval, 1, 20, Token);
        Assert.Equal(1, accountReport.Summary.Starts); Assert.Equal(1, accountReport.Summary.RecordedPulses); Assert.Equal(detailCutoff, accountReport.AvailableFromUtc);
        Assert.Equal(generalCutoff, before.AvailableFromUtc); Assert.Equal(detailCutoff, before.DetailAvailableFromUtc); Assert.Equal(365, before.Daily.Length);
        var recent = new ConsumptionReportInterval(DateOnly.FromDateTime(detailCutoff.UtcDateTime), new(2026, 10, 8));
        Assert.Equal(1, (await reports.GetAsync(recent, Token)).Summary.AccountsWithActivity);
        var dailyColumns = await context.Database.SqlQueryRaw<string>("SELECT column_name AS \"Value\" FROM information_schema.columns WHERE table_schema='catalog' AND table_name='ConsumptionDaily'").ToArrayAsync(Token);
        Assert.DoesNotContain("AccountId", dailyColumns); Assert.DoesNotContain("SessionId", dailyColumns); Assert.DoesNotContain("AuthenticationBindingHash", dailyColumns);
    }
    [Fact]
    public async Task ExpiredEightHourReceiptsCannotBeResumedAndPruningPreservesAccountAndGeneralFacts()
    {
        await fixture.ResetAsync(Token); var clock = new ActivityClock(Now.AddHours(-8)); var account = Guid.NewGuid();
        await using var context = fixture.Context(); var work = await Publish(context, clock, "consumo-receipt-expiry"); var recorder = Recording(context, clock);
        var session = (await recorder.StartAsync(account, Binding, work.Slug, new(Guid.NewGuid(), work.Version), Token)).Value!;
        clock.Advance(TimeSpan.FromSeconds(1)); Assert.True((await recorder.PulseAsync(account, Binding, session.SessionId, Reading(), Token)).Succeeded);
        clock.Set(Now);
        Assert.Equal(404, (await recorder.PulseAsync(account, Binding, session.SessionId, Reading(), Token)).Status);
        var general = await context.ConsumptionDaily.AsNoTracking().SingleAsync(Token); var personal = await context.ConsumptionAccountDaily.AsNoTracking().SingleAsync(Token);
        async Task<ConsumptionPruneResult> Prune()
        {
            await using var isolated = fixture.Context(); return await new ConsumptionRetentionService(isolated, clock).PruneAsync(Token);
        }
        var outcomes = await Task.WhenAll(Prune(), Prune());
        Assert.Equal(1, outcomes.Sum(x => x.PulsesDeleted)); Assert.Equal(1, outcomes.Sum(x => x.SessionsDeleted));
        Assert.Empty(await context.ConsumptionSessions.AsNoTracking().ToArrayAsync(Token));
        Assert.Equal(general.RecordedPulses, (await context.ConsumptionDaily.AsNoTracking().SingleAsync(Token)).RecordedPulses);
        Assert.Equal(personal.RecordedPulses, (await context.ConsumptionAccountDaily.AsNoTracking().SingleAsync(Token)).RecordedPulses);
    }
    [Fact]
    public async Task CancellationBeforeRecordingOrReportingLeavesNoFactsAndAllowsExplicitRetry()
    {
        await fixture.ResetAsync(Token); var clock = new ActivityClock(Now);
        await using var context = fixture.Context(); var work = await Publish(context, clock, "consumo-cancel");
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(Token); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Recording(context, clock).StartAsync(Guid.NewGuid(), Binding, work.Slug, new(Guid.NewGuid(), work.Version), cancelled.Token));
        Assert.Null(context.Database.CurrentTransaction); Assert.Empty(context.ChangeTracker.Entries()); Assert.Equal(0, await context.ConsumptionSessions.CountAsync(Token));
        var report = new ConsumptionActivityReportService(context, clock, Enabled()); var interval = new ConsumptionReportInterval(new(2026, 10, 7), new(2026, 10, 8));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => report.GetAsync(interval, cancelled.Token));
        Assert.Equal(0, (await report.GetAsync(interval, Token)).Summary.Starts); Assert.Equal(3, context.Database.GetCommandTimeout());
    }

    [Theory, InlineData(false), InlineData(true)]
    public async Task RealPruneFailureRestoresReceiptsSessionsAndBothDailyTables(bool failAfterGeneralDelete)
    {
        await fixture.ResetAsync(Token); var clock = new ActivityClock(Now.AddDays(-400)); var account = Guid.NewGuid();
        await using (var setup = fixture.Context())
        {
            var work = await Publish(setup, clock, "consumo-prune-rollback"); var recorder = Recording(setup, clock);
            var session = (await recorder.StartAsync(account, Binding, work.Slug, new(Guid.NewGuid(), work.Version), Token)).Value!;
            clock.Advance(TimeSpan.FromSeconds(1)); Assert.True((await recorder.PulseAsync(account, Binding, session.SessionId, Reading(), Token)).Succeeded);
        }
        clock.Set(Now);
        await using (var failed = fixture.Context(interceptor: new FailAfterPruneDelete(failAfterGeneralDelete)))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => new ConsumptionRetentionService(failed, clock).PruneAsync(Token));
            Assert.Null(failed.Database.CurrentTransaction); Assert.Empty(failed.ChangeTracker.Entries());
            Assert.Equal(1, await failed.ConsumptionPulses.CountAsync(Token)); Assert.Equal(1, await failed.ConsumptionSessions.CountAsync(Token));
            Assert.Equal(1, await failed.ConsumptionAccountDaily.CountAsync(Token)); Assert.Equal(1, await failed.ConsumptionDaily.CountAsync(Token));
        }
        await using var recovered = fixture.Context(); Assert.Equal(new ConsumptionPruneResult(1, 1, 1, 1), await new ConsumptionRetentionService(recovered, clock).PruneAsync(Token));
        Assert.Equal(3, recovered.Database.GetCommandTimeout());
    }
    [Fact]
    public async Task ReceiptBatchIsBoundedAndParentSessionWaitsUntilAllChildrenAreGone()
    {
        await fixture.ResetAsync(Token); var clock = new ActivityClock(Now.AddHours(-8)); var account = Guid.NewGuid();
        await using var context = fixture.Context(); var work = await Publish(context, clock, "consumo-prune-batch"); var recorder = Recording(context, clock);
        var session = (await recorder.StartAsync(account, Binding, work.Slug, new(Guid.NewGuid(), work.Version), Token)).Value!;
        clock.Advance(TimeSpan.FromSeconds(1)); Assert.True((await recorder.PulseAsync(account, Binding, session.SessionId, Reading(), Token)).Succeeded);
        // Synthetic zero-credit receipts exercise the real LIMIT without thousands of HTTP calls.
        // Keep both additive ledgers and the session sequence consistent with the fixture.
        var received = clock.GetUtcNow();
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO catalog."ConsumptionPulses" ("SessionId","Sequence","CanonicalHash","ReceivedUtc","CreditedMs","ProgressBasisPoints","CoverageIncomplete","EndedReported","EndedNow")
            SELECT {session.SessionId},n,{Binding},{received},0,NULL,false,false,false FROM generate_series(2,1001) AS n;
            """, Token);
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE catalog."ConsumptionSessions" SET "LastSequence"=1001 WHERE "Id"={session.SessionId};
            """, Token);
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE catalog."ConsumptionDaily" SET "RecordedPulses"="RecordedPulses"+1000,"UnknownProgressSamples"="UnknownProgressSamples"+1000 WHERE "ContentId"={work.Id};
            """, Token);
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE catalog."ConsumptionAccountDaily" SET "RecordedPulses"="RecordedPulses"+1000,"UnknownProgressSamples"="UnknownProgressSamples"+1000 WHERE "ContentId"={work.Id};
            """, Token);
        clock.Set(Now); var retention = new ConsumptionRetentionService(context, clock);
        Assert.Equal(new ConsumptionPruneResult(1000, 0, 0, 0), await retention.PruneAsync(Token));
        Assert.Equal(1, await context.ConsumptionPulses.CountAsync(Token)); Assert.Equal(1, await context.ConsumptionSessions.CountAsync(Token));
        Assert.Equal(new ConsumptionPruneResult(1, 1, 0, 0), await retention.PruneAsync(Token));
        Assert.Equal(1001, (await context.ConsumptionDaily.AsNoTracking().SingleAsync(Token)).RecordedPulses);
        Assert.Equal(1001, (await context.ConsumptionAccountDaily.AsNoTracking().SingleAsync(Token)).RecordedPulses);
    }
    private static IOptions<ConsumptionRecordingOptions> Enabled() => Options.Create(new ConsumptionRecordingOptions { RecordingEnabled = true });
    private static ConsumptionRecordingService Recording(CatalogDbContext context, TimeProvider clock, bool enabled = true) => new(context, clock, Options.Create(new ConsumptionRecordingOptions { RecordingEnabled = enabled }));
    private async Task<AdminContentView> Publish(CatalogDbContext context, TimeProvider clock, string slug)
    {
        var service = new CatalogService(context, clock); var draft = (await service.CreateAsync(Guid.NewGuid(), new(slug, "Título de QA", "Resumen", "Sinopsis", "lecturas", WorkText: "Obra privada sintética."), Token)).Value!;
        return (await service.UpdateAsync(Guid.NewGuid(), draft.Id, Change(draft, "published"), Token)).Value!;
    }
    private static UpdateContentRequest Change(AdminContentView x, string status) => new(x.Version, status, x.Slug, x.Title, x.Summary, x.Body, x.Category, x.CoverAsset, x.DurationSeconds, x.Author, x.Tags, x.WorkText, x.YouTubeId, x.CollectionKind, x.ItemIds);
    private static ConsumptionPulseRequest Reading(int coverage = 5000) => new(1, 1000, 1000, Reading: new(coverage, [new(0, coverage)]));
    private sealed class ActivityClock(DateTimeOffset initial) : TimeProvider
    {
        private DateTimeOffset now = initial;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan span) => now += span;
        public void Set(DateTimeOffset value) => now = value;
    }

    private sealed class FailAfterPruneDelete(bool general) : DbCommandInterceptor
    {
        public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default) =>
            command.CommandText.Contains(general ? "DELETE FROM catalog.\"ConsumptionDaily\"" : "DELETE FROM catalog.\"ConsumptionPulses\"", StringComparison.Ordinal) ? throw new InvalidOperationException("Synthetic failure after real retention delete.") : ValueTask.FromResult(result);
    }
    private sealed class FailAfterDailyWrite(bool accountDaily) : DbCommandInterceptor
    {
        private bool failed;
        public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (!failed && command.CommandText.Contains(accountDaily ? "INSERT INTO catalog.\"ConsumptionAccountDaily\"" : "INSERT INTO catalog.\"ConsumptionDaily\"", StringComparison.Ordinal))
            {
                failed = true;
                throw new InvalidOperationException("Synthetic one-shot failure after real daily write.");
            }
            return ValueTask.FromResult(result);
        }
    }
}
