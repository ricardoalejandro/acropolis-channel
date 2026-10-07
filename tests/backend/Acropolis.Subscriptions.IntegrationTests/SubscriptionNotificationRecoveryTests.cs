using Acropolis.Identity.IntegrationTests;
using Acropolis.Migrations;
using Acropolis.Subscriptions.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Acropolis.Subscriptions.IntegrationTests;

[Collection("IdentityPostgres")]
public sealed class SubscriptionNotificationRecoveryTests(IdentityFixture database)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task RecoveryCancelsEveryRestoredOutboxStateAndQuarantinesAllTermsIdempotently()
    {
        await database.ResetAsync(Token); await using var api = new SubscriptionNotificationTestHost(database);
        var actor = await api.AccountAsync("restore-notice-manager", manager: true);
        var statuses = new[] { "pending", "sending", "sent", "failed", "cancelled" };
        for (var index = 0; index < statuses.Length; index++)
        {
            var member = await api.AccountAsync("restore-notice-holder-" + index);
            Assert.True((await api.AssignAsync(actor.Id, member.Id, "annual", api.Clock.GetUtcNow().AddDays(7).AddYears(-1))).Succeeded);
        }
        Guid[] sourceIds;
        await using (var setup = Context(true))
        {
            var outbox = await setup.Notifications.OrderBy(row => row.UserId).ToArrayAsync(Token);
            sourceIds = outbox.Select(row => row.Id).Order().ToArray();
            for (var index = 0; index < statuses.Length; index++)
            {
                outbox[index].Status = statuses[index]; outbox[index].Attempts = statuses[index] == "failed" ? 5 : 1;
                if (statuses[index] == "sending")
                {
                    outbox[index].LeaseOwner = "restored_synthetic_worker"; outbox[index].LeaseExpiresUtc = api.Clock.GetUtcNow().AddHours(1);
                }
                var subscription = await setup.Subscriptions.SingleAsync(row => row.Id == outbox[index].SubscriptionId, Token);
                subscription.Status = index % 3 == 0 ? "active" : index % 3 == 1 ? "cancelled" : "suspended";
            }
            await setup.SaveChangesAsync(Token);
        }
        await RecoverAsync(api);
        Guid[] firstNotificationIds; Guid[] auditIds; string[] versions;
        await using (var context = Context())
        {
            var restored = await context.Notifications.AsNoTracking().Where(row => sourceIds.Contains(row.Id)).ToArrayAsync(Token);
            Assert.Equal(statuses.Length, restored.Length);
            Assert.All(restored, row => { Assert.Equal("cancelled", row.Status); Assert.Null(row.LeaseOwner); Assert.Null(row.LeaseExpiresUtc); });
            var terms = await context.Subscriptions.AsNoTracking().ToArrayAsync(Token);
            Assert.All(terms, row => Assert.NotEqual("active", row.Status));
            var tombstones = await context.Notifications.AsNoTracking().Where(row => row.Kind == "expiring").ToArrayAsync(Token);
            Assert.Equal(statuses.Length, tombstones.Length);
            foreach (var term in terms)
            {
                var tombstone = Assert.Single(tombstones, row => row.SubscriptionId == term.Id && row.TermGeneration == term.NotificationTermGeneration);
                Assert.Equal("cancelled", tombstone.Status);
            }
            firstNotificationIds = await context.Notifications.OrderBy(row => row.Id).Select(row => row.Id).ToArrayAsync(Token);
            auditIds = await context.Audit.OrderBy(row => row.Id).Select(row => row.Id).ToArrayAsync(Token);
            versions = await context.Subscriptions.OrderBy(row => row.Id).Select(row => row.Version).ToArrayAsync(Token);
        }
        await RecoverAsync(api); Assert.Equal(0, await api.ScheduleAsync()); Assert.False(await api.DispatchAsync());
        Assert.Empty(api.Sender.Messages); Assert.Empty(api.Sender.AttemptedIds);
        await using var repeated = Context();
        Assert.Equal(firstNotificationIds, await repeated.Notifications.OrderBy(row => row.Id).Select(row => row.Id).ToArrayAsync(Token));
        Assert.Equal(auditIds, await repeated.Audit.OrderBy(row => row.Id).Select(row => row.Id).ToArrayAsync(Token));
        Assert.Equal(versions, await repeated.Subscriptions.OrderBy(row => row.Id).Select(row => row.Version).ToArrayAsync(Token));
    }

    [Fact]
    public async Task ReactivatingRestoredStatusDoesNotResendOldTermButAGenuineRenewalCreatesANewEligiblePeriod()
    {
        await database.ResetAsync(Token); await using var api = new SubscriptionNotificationTestHost(database);
        var actor = await api.AccountAsync("restore-renew-notice-manager", manager: true);
        var member = await api.AccountAsync("restore-renew-notice-holder");
        var old = (await api.AssignAsync(actor.Id, member.Id, "annual", api.Clock.GetUtcNow().AddDays(7).AddYears(-1))).Value!;
        await RecoverAsync(api);
        var restored = await api.CurrentAsync(member.Id);
        var reactivated = (await api.UpdateAsync(actor.Id, restored, "active")).Value!;
        Assert.Equal(old.StartsUtc, reactivated.StartsUtc); Assert.Equal(old.ExpiresUtc, reactivated.ExpiresUtc);
        Assert.Equal(0, await api.ScheduleAsync()); Assert.False(await api.DispatchAsync()); Assert.Empty(api.Sender.Messages);
        Guid previousGeneration;
        await using (var context = Context()) previousGeneration = (await context.Subscriptions.AsNoTracking().SingleAsync(Token)).NotificationTermGeneration;
        api.Clock.Advance(TimeSpan.FromSeconds(15)); // Recovery reserves the first safe submission window.
        var renewed = (await api.AssignAsync(actor.Id, member.Id, "annual", reactivated.ExpiresUtc, reactivated.Version)).Value!;
        Assert.True(await api.DispatchAsync()); Assert.Single(api.Sender.Messages);
        await using (var context = Context())
        {
            var row = await context.Subscriptions.AsNoTracking().SingleAsync(Token);
            Assert.NotEqual(previousGeneration, row.NotificationTermGeneration);
            Assert.Equal(2, await context.Notifications.CountAsync(item => item.Kind != "expiring", Token));
        }
        api.Clock.Advance(renewed.ExpiresUtc!.Value - api.Clock.GetUtcNow() - TimeSpan.FromDays(7));
        Assert.Equal(1, await api.ScheduleAsync()); Assert.Equal(0, await api.ScheduleAsync()); Assert.True(await api.DispatchAsync());
        Assert.Equal(2, api.Sender.Messages.Count);
        await using var final = Context();
        var reminders = await final.Notifications.AsNoTracking().Where(row => row.Kind == "expiring").ToArrayAsync(Token);
        Assert.Equal(2, reminders.Length); Assert.Single(reminders, row => row.Status == "cancelled"); Assert.Single(reminders, row => row.Status == "sent");
    }

    [Fact]
    public async Task RecoveryOutboxFailureRollsBackSuspensionAuditAndSuppressionTogether()
    {
        await database.ResetAsync(Token); await using var api = new SubscriptionNotificationTestHost(database);
        var actor = await api.AccountAsync("restore-fail-notice-manager", manager: true);
        var member = await api.AccountAsync("restore-fail-notice-holder");
        var old = (await api.AssignAsync(actor.Id, member.Id, "annual", api.Clock.GetUtcNow())).Value!;
        const string cleanup = "DROP TRIGGER IF EXISTS qa_notification_restore_failure ON subscriptions.\"NotificationOutbox\"; DROP FUNCTION IF EXISTS subscriptions.qa_notification_restore_failure();";
        try
        {
            await database.ExecuteAsync("""
                CREATE FUNCTION subscriptions.qa_notification_restore_failure() RETURNS trigger LANGUAGE plpgsql AS $qa$
                BEGIN RAISE EXCEPTION 'Synthetic notification recovery failure' USING ERRCODE='23514'; END; $qa$;
                CREATE TRIGGER qa_notification_restore_failure BEFORE UPDATE OF "Status" ON subscriptions."NotificationOutbox"
                FOR EACH ROW WHEN (NEW."Status"='cancelled' AND OLD."Status"!='cancelled')
                EXECUTE FUNCTION subscriptions.qa_notification_restore_failure();
                """, Token);
            var error = await Assert.ThrowsAsync<PostgresException>(() => RecoverAsync(api)); Assert.Equal("23514", error.SqlState);
            await using var context = Context(); var unchanged = await context.Subscriptions.AsNoTracking().SingleAsync(Token);
            Assert.Equal("active", unchanged.Status); Assert.Equal(old.Version, unchanged.Version);
            Assert.Single(await context.Audit.AsNoTracking().ToArrayAsync(Token));
            Assert.Equal("pending", (await context.Notifications.AsNoTracking().SingleAsync(Token)).Status);
        }
        finally { await database.ExecuteAsync(cleanup, CancellationToken.None); }
        await RecoverAsync(api); Assert.False(await api.DispatchAsync()); Assert.Empty(api.Sender.Messages);
    }

    [Fact]
    public async Task RuntimeRoleCanProcessOutboxButCannotDeleteHistoryOrApplyDdl()
    {
        await database.ResetAsync(Token); await using var api = new SubscriptionNotificationTestHost(database);
        var actor = await api.AccountAsync("acl-notice-manager", manager: true);
        var member = await api.AccountAsync("acl-notice-holder");
        Assert.True((await api.AssignAsync(actor.Id, member.Id, "annual", api.Clock.GetUtcNow())).Succeeded);
        Assert.True(await api.DispatchAsync()); Assert.Single(api.Sender.Messages);
        await using var context = Context();
        var deniedDelete = await Assert.ThrowsAsync<PostgresException>(() => context.Database.ExecuteSqlRawAsync("DELETE FROM subscriptions.\"NotificationOutbox\"", Token));
        Assert.Equal("42501", deniedDelete.SqlState);
        var deniedDdl = await Assert.ThrowsAsync<PostgresException>(() => context.Database.ExecuteSqlRawAsync("ALTER TABLE subscriptions.\"NotificationOutbox\" ADD COLUMN qa_forbidden integer", Token));
        Assert.Equal("42501", deniedDdl.SqlState);
        Assert.Equal("sent", (await context.Notifications.AsNoTracking().SingleAsync(Token)).Status);
    }

    private async Task RecoverAsync(SubscriptionNotificationTestHost api)
    {
        await using var context = Context(true);
        await new SubscriptionOperations(context, api.Clock).InvalidateRecoveryAsync(Token);
    }
    private SubscriptionsDbContext Context(bool migration = false)
    {
        var options = new DbContextOptionsBuilder<SubscriptionsDbContext>();
        SubscriptionsRegistration.ConfigureDatabase(options, migration ? database.MigrationConnection : database.RuntimeConnection);
        return new(options.Options);
    }
}
