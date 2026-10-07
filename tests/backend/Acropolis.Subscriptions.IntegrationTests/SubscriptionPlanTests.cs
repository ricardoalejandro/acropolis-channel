using Acropolis.Identity.Infrastructure;
using Acropolis.Identity.IntegrationTests;
using Acropolis.Migrations;
using Acropolis.Subscriptions.Application;
using Acropolis.Subscriptions.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Acropolis.Subscriptions.IntegrationTests;

[Collection("IdentityPostgres")]
public sealed class SubscriptionPlanTests(IdentityFixture database)
{
    private CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("probationismo")]
    [InlineData("annual")]
    public async Task EarlyManualRenewalPreservesCurrentCoverageAndExactRetriesDoNotShortenOrExtendAgain(string plan)
    {
        await database.ResetAsync(Token); await using var api = new IdentityApiFactory(database);
        api.Clock.Advance(TimeSpan.FromTicks(-(api.Clock.GetUtcNow().Ticks % 10)));
        var actor = await User(api, "plan-owner@example.test"); var member = await User(api, "plan-member@example.test");
        await Bootstrap(api, actor);
        var starts = api.Clock.GetUtcNow();
        var assigned = (await Assign(api, actor.Id, member.Id, new(plan, starts, null, "Asignación manual QA"))).Value!;
        Assert.Equal(starts, assigned.StartsUtc); Assert.Equal(SubscriptionRules.ExpiresFor(plan, starts), assigned.ExpiresUtc);
        Assert.Equal("active", assigned.EffectiveState); Assert.Equal(SubscriptionAccessScope.FullCatalog, await Access(api, member.Id));
        var end = assigned.ExpiresUtc!.Value;
        var renewed = (await Assign(api, actor.Id, member.Id, new(plan, end, assigned.Version, "Renovación anticipada QA"))).Value!;
        Assert.Equal(starts, renewed.StartsUtc); Assert.Equal(SubscriptionRules.ExpiresFor(plan, end), renewed.ExpiresUtc);
        Assert.NotEqual(assigned.Version, renewed.Version); Assert.Equal("active", renewed.EffectiveState);
        Assert.Equal(SubscriptionAccessScope.FullCatalog, await Access(api, member.Id));
        var duplicate = await Assign(api, actor.Id, member.Id, new(plan, end, assigned.Version, "Duplicado explícito QA"));
        Assert.Equal("concurrency_conflict", duplicate.Error); Assert.Equal(409, duplicate.Status);
        var originalNoop = (await Assign(api, actor.Id, member.Id, new(plan, starts, renewed.Version, "Misma asignación QA"))).Value!;
        var renewalNoop = (await Assign(api, actor.Id, member.Id, new(plan, end, renewed.Version, "Misma renovación QA"))).Value!;
        Assert.Equal(renewed.Version, originalNoop.Version); Assert.Equal(renewed.ExpiresUtc, originalNoop.ExpiresUtc);
        Assert.Equal(renewed.Version, renewalNoop.Version); Assert.Equal(renewed.ExpiresUtc, renewalNoop.ExpiresUtc);
        var replacement = await Assign(api, actor.Id, member.Id, new(plan, starts.AddDays(1), renewed.Version, "Reemplazo futuro QA"));
        Assert.Equal("active_period_would_be_replaced", replacement.Error); Assert.Equal(409, replacement.Status);
        await using (var context = Context())
        {
            var audit = await context.Audit.AsNoTracking().OrderBy(x => x.CreatedUtc).ThenBy(x => x.Id).ToArrayAsync(Token);
            Assert.Equal(2, audit.Length);
            var renewal = Assert.Single(audit, x => x.Action == "subscription.renewed");
            Assert.Equal(plan, renewal.BeforePlan); Assert.Equal(plan, renewal.AfterPlan);
            Assert.Equal(starts, renewal.BeforeStartsUtc); Assert.Equal(starts, renewal.AfterStartsUtc);
            Assert.Equal(end, renewal.BeforeExpiresUtc); Assert.Equal(renewed.ExpiresUtc, renewal.AfterExpiresUtc);
            var report = new SubscriptionReportService(context, api.Clock);
            var date = DateOnly.FromDateTime(starts.UtcDateTime);
            var events = await report.GetEventsAsync(new(date, date.AddDays(1)), Token);
            Assert.Equal(2, events.TotalEvents);
            Assert.Equal(1, Assert.Single(events.ByEvent, x => x.Key == "assigned").Count);
            Assert.Equal(1, Assert.Single(events.ByEvent, x => x.Key == "renewed").Count);
            Assert.Equal(0, Assert.Single(events.ByEvent, x => x.Key == "unclassified").Count);
        }
        api.Clock.Advance(end - api.Clock.GetUtcNow());
        Assert.Equal(SubscriptionAccessScope.FullCatalog, await Access(api, member.Id)); // The original boundary remains covered.
        api.Clock.Advance(renewed.ExpiresUtc!.Value - api.Clock.GetUtcNow());
        Assert.Equal(SubscriptionAccessScope.None, await Access(api, member.Id)); // The renewed final boundary is exclusive.
        using var scope = api.Services.CreateScope();
        var mine = await scope.ServiceProvider.GetRequiredService<ISubscriptionService>().GetMineAsync(member.Id, Token);
        Assert.Equal("active", mine.Subscription!.Status); Assert.Equal("expired", mine.Subscription.EffectiveState);
        Assert.Equal(renewed.Version, mine.Subscription.Version); Assert.False(mine.EligibleToActivate);
        Assert.Equal("plan_change_requires_manager", (await scope.ServiceProvider.GetRequiredService<ISubscriptionService>().ActivateAsync(member.Id, Token)).Error);
    }

    [Fact]
    public async Task FreeAssignmentUsesRealServerTimeAndCannotReactivateOrRenewAStoredManualPlan()
    {
        await database.ResetAsync(Token); await using var api = new IdentityApiFactory(database);
        api.Clock.Advance(TimeSpan.FromTicks(-(api.Clock.GetUtcNow().Ticks % 10)));
        var actor = await User(api, "free-plan-owner@example.test"); var member = await User(api, "free-plan-member@example.test");
        await Bootstrap(api, actor);
        var now = api.Clock.GetUtcNow();
        var assigned = (await Assign(api, actor.Id, member.Id, new("annual", now, null, "Plan anual QA"))).Value!;
        using (var scope = api.Services.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ISubscriptionService>();
            var cancelled = (await service.CancelAsync(member.Id, new(assigned.Version), Token)).Value!;
            Assert.Equal("plan_change_requires_manager", (await service.ActivateAsync(member.Id, Token)).Error);
            Assert.Equal(SubscriptionAccessScope.None, await Access(api, member.Id));
            assigned = cancelled;
        }
        var free = (await Assign(api, actor.Id, member.Id, new("free_beta", null, assigned.Version, "Cambio explícito a gratuito QA"))).Value!;
        Assert.Equal(now, free.StartsUtc); Assert.Null(free.ExpiresUtc); Assert.Equal("active", free.EffectiveState);
        Assert.Equal(SubscriptionAccessScope.FreeOnly, await Access(api, member.Id));
        api.Clock.Advance(TimeSpan.FromSeconds(1));
        var noop = (await Assign(api, actor.Id, member.Id, new("free_beta", null, free.Version, "Repetición gratuito QA"))).Value!;
        Assert.Equal(free.StartsUtc, noop.StartsUtc); Assert.Equal(free.Version, noop.Version);
        Assert.Equal("validation_error", (await Assign(api, actor.Id, member.Id, new("free_beta", now, free.Version, "Fecha gratuita enviada QA"))).Error);
        await using var context = Context();
        Assert.Equal(3, await context.Audit.CountAsync(Token));
        var snapshot = await new SubscriptionReportService(context, api.Clock).GetCurrentAsync(Token);
        Assert.Equal(1, snapshot.Total); Assert.Equal(1, Assert.Single(snapshot.ByEffectiveState!, x => x.Key == "active").Count);
    }

    [Fact]
    public async Task ConcurrentFirstAssignmentsHaveOneVersionedWinnerAndNoOrphanAudit()
    {
        await database.ResetAsync(Token); await using var api = new IdentityApiFactory(database);
        api.Clock.Advance(TimeSpan.FromTicks(-(api.Clock.GetUtcNow().Ticks % 10)));
        var actor = await User(api, "concurrent-plan-owner@example.test"); var member = await User(api, "concurrent-plan-member@example.test");
        await Bootstrap(api, actor); var starts = api.Clock.GetUtcNow();
        var first = Assign(api, actor.Id, member.Id, new("annual", starts, null, "Asignación concurrente anual QA"));
        var second = Assign(api, actor.Id, member.Id, new("probationismo", starts, null, "Asignación concurrente probacionismo QA"));
        var results = await Task.WhenAll(first, second);
        var winner = Assert.Single(results, x => x.Succeeded).Value!;
        Assert.Equal("concurrency_conflict", Assert.Single(results, x => !x.Succeeded).Error);
        await using var context = Context();
        var row = Assert.Single(await context.Subscriptions.AsNoTracking().ToArrayAsync(Token));
        Assert.Equal(winner.Version, row.Version); Assert.Equal(winner.Plan, row.Plan);
        var audit = Assert.Single(await context.Audit.AsNoTracking().ToArrayAsync(Token));
        Assert.Equal(row.Id, audit.SubscriptionId); Assert.Equal("subscription.assigned", audit.Action);
        Assert.Null(audit.BeforePlan); Assert.Equal(row.Plan, audit.AfterPlan);
    }

    [Fact]
    public async Task FailedPlanWriteRollsBackTermsVersionAndAuditAndRetryIsExplicit()
    {
        await database.ResetAsync(Token); await using var api = new IdentityApiFactory(database);
        api.Clock.Advance(TimeSpan.FromTicks(-(api.Clock.GetUtcNow().Ticks % 10)));
        var actor = await User(api, "rollback-plan-owner@example.test"); var member = await User(api, "rollback-plan-member@example.test");
        await Bootstrap(api, actor);
        var free = (await Assign(api, actor.Id, member.Id, new("free_beta", null, null, "Gratuito inicial QA"))).Value!;
        const string remove = "DROP TRIGGER IF EXISTS qa_plan_failure ON subscriptions.\"Subscriptions\"; DROP FUNCTION IF EXISTS subscriptions.qa_plan_failure();";
        try
        {
            await database.ExecuteAsync("""
                CREATE FUNCTION subscriptions.qa_plan_failure() RETURNS trigger LANGUAGE plpgsql AS $qa$
                BEGIN RAISE EXCEPTION 'Synthetic plan write failure' USING ERRCODE='23514'; END; $qa$;
                CREATE TRIGGER qa_plan_failure BEFORE UPDATE OF "Plan" ON subscriptions."Subscriptions"
                FOR EACH ROW WHEN (NEW."Plan" IS DISTINCT FROM OLD."Plan") EXECUTE FUNCTION subscriptions.qa_plan_failure();
                """, Token);
            var error = await Assert.ThrowsAsync<DbUpdateException>(() => Assign(api, actor.Id, member.Id, new("annual", api.Clock.GetUtcNow(), free.Version, "Asignación que falla QA")));
            Assert.Equal("23514", Assert.IsType<PostgresException>(error.InnerException).SqlState);
            await using var context = Context(); var row = await context.Subscriptions.AsNoTracking().SingleAsync(Token);
            Assert.Equal(free.Version, row.Version); Assert.Equal("free_beta", row.Plan); Assert.Null(row.ExpiresUtc);
            Assert.Single(await context.Audit.AsNoTracking().ToArrayAsync(Token));
        }
        finally { await database.ExecuteAsync(remove, CancellationToken.None); }
        Assert.True((await Assign(api, actor.Id, member.Id, new("annual", api.Clock.GetUtcNow(), free.Version, "Reintento explícito QA"))).Succeeded);
    }

    [Fact]
    public async Task LegacyMigrationBackfillsRealActivationWithoutInventingAuditAndCannotDiscardManualTermHistory()
    {
        await database.ResetAsync(Token);
        var id = Guid.NewGuid(); var account = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        now = new DateTimeOffset(now.UtcTicks - now.UtcTicks % 10, TimeSpan.Zero);
        await using (var old = Context(true))
        {
            await old.GetService<IMigrator>().MigrateAsync("20261006074504_InitialSubscriptions", Token);
            await old.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO subscriptions."Subscriptions" ("Id","UserId","Plan","Status","CreatedUtc","ActivatedUtc","UpdatedUtc","Version")
                VALUES ({id},{account},'free_beta','active',{now},{now},{now},'legacy-version')
                """, Token);
        }
        await new ChannelMigrationRunner().RunAsync(database.MigrationConnection, Token);
        await new ChannelMigrationRunner().RunAsync(database.MigrationConnection, Token);
        await using (var verify = Context())
        {
            Assert.False(verify.Database.HasPendingModelChanges());
            var row = await verify.Subscriptions.AsNoTracking().SingleAsync(Token);
            Assert.Equal(id, row.Id); Assert.Equal("legacy-version", row.Version);
            Assert.Equal(now, row.StartsUtc); Assert.Null(row.ExpiresUtc); Assert.Equal("free_beta", row.Plan);
            Assert.Empty(await verify.Audit.AsNoTracking().ToArrayAsync(Token));
        }
        await using (var setup = Context())
        {
            var row = await setup.Subscriptions.SingleAsync(Token);
            row.Plan = "annual"; row.ExpiresUtc = row.StartsUtc.AddYears(1);
            setup.Audit.Add(new SubscriptionAudit
            {
                Id = Guid.NewGuid(),
                SubscriptionId = row.Id,
                UserId = row.UserId,
                ActorId = Guid.NewGuid(),
                Action = "subscription.assigned",
                AfterStatus = "active",
                Reason = "Historial manual sintético QA",
                CreatedUtc = now,
                AfterPlan = "annual",
                AfterStartsUtc = now,
                AfterExpiresUtc = row.ExpiresUtc
            });
            await setup.SaveChangesAsync(Token);
            row.Plan = "free_beta"; row.ExpiresUtc = null; await setup.SaveChangesAsync(Token);
        }
        await using (var downgrade = Context(true))
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => downgrade.GetService<IMigrator>().MigrateAsync("20261006074504_InitialSubscriptions", Token));
            Assert.Equal("23514", error.SqlState);
        }
        await using var preserved = Context();
        Assert.Contains("20261007210100_AddSubscriptionPlans", await preserved.Database.GetAppliedMigrationsAsync(Token));
        Assert.Equal("annual", (await preserved.Audit.AsNoTracking().SingleAsync(Token)).AfterPlan);
        Assert.False(preserved.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task RecoverySuspendsEveryStoredActivePlanWithoutClearingItsTermsOrRewritingHistoryOnRepeat()
    {
        await database.ResetAsync(Token); await using var api = new IdentityApiFactory(database);
        api.Clock.Advance(TimeSpan.FromTicks(-(api.Clock.GetUtcNow().Ticks % 10)));
        var actor = await User(api, "recovery-plan-owner@example.test"); var member = await User(api, "recovery-plan-member@example.test");
        await Bootstrap(api, actor);
        var assigned = (await Assign(api, actor.Id, member.Id, new("annual", api.Clock.GetUtcNow(), null, "Plan restaurado QA"))).Value!;
        await using (var maintenance = Context(true)) await new SubscriptionOperations(maintenance, api.Clock).InvalidateRecoveryAsync(Token);
        await using (var maintenance = Context(true)) await new SubscriptionOperations(maintenance, api.Clock).InvalidateRecoveryAsync(Token);
        Assert.Equal(SubscriptionAccessScope.None, await Access(api, member.Id));
        await using var context = Context(); var row = await context.Subscriptions.AsNoTracking().SingleAsync(Token);
        Assert.Equal("suspended", row.Status); Assert.Equal(assigned.StartsUtc, row.StartsUtc); Assert.Equal(assigned.ExpiresUtc, row.ExpiresUtc);
        var audit = Assert.Single(await context.Audit.Where(x => x.Action == "subscription.recovery_suspended").ToArrayAsync(Token));
        Assert.Equal("annual", audit.BeforePlan); Assert.Equal("annual", audit.AfterPlan);
        Assert.Equal(assigned.ExpiresUtc, audit.BeforeExpiresUtc); Assert.Equal(assigned.ExpiresUtc, audit.AfterExpiresUtc);
    }

    private SubscriptionsDbContext Context(bool migration = false)
    {
        var options = new DbContextOptionsBuilder<SubscriptionsDbContext>();
        SubscriptionsRegistration.ConfigureDatabase(options, migration ? database.MigrationConnection : database.RuntimeConnection);
        return new(options.Options);
    }
    private async Task<ChannelUser> User(IdentityApiFactory api, string email)
    {
        using var scope = api.Services.CreateScope(); var manager = scope.ServiceProvider.GetRequiredService<UserManager<ChannelUser>>();
        var user = new ChannelUser { Id = Guid.NewGuid(), Email = email, UserName = email, DisplayName = "QA Plan", EmailConfirmed = true };
        Assert.True((await manager.CreateAsync(user, "Synthetic plan account phrase 2026")).Succeeded); return user;
    }
    private async Task Bootstrap(IdentityApiFactory api, ChannelUser user)
    {
        await using var identity = database.Context(true);
        await new IdentityOperations(identity, api.Clock).BootstrapOwnerAsync(user.Email!, user.Email, Token);
    }
    private async Task<SubscriptionResult<SubscriptionView>> Assign(IdentityApiFactory api, Guid actor, Guid user, AssignSubscriptionRequest request)
    {
        using var scope = api.Services.CreateScope(); return await scope.ServiceProvider.GetRequiredService<ISubscriptionService>().AssignAsync(actor, user, request, Token);
    }
    private async Task<SubscriptionAccessScope> Access(IdentityApiFactory api, Guid user)
    {
        using var scope = api.Services.CreateScope(); return await scope.ServiceProvider.GetRequiredService<ISubscriptionAccess>().GetScopeAsync(user, Token);
    }
}
