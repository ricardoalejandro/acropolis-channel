using Acropolis.Identity.IntegrationTests;
using Acropolis.Subscriptions.Application;
using Acropolis.Subscriptions.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Acropolis.Subscriptions.IntegrationTests;

[Collection("IdentityPostgres")]
public sealed class SubscriptionNotificationDeliveryTests(IdentityFixture database)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task DisabledNotificationsDoNotQueueScheduleSendOrConsumeExistingDeliveryState()
    {
        await database.ResetAsync(Token);
        await using var api = new SubscriptionNotificationTestHost(database, enabled: false);
        var actor = await api.AccountAsync("disabled-notice-manager", manager: true);
        var member = await api.AccountAsync("disabled-notice-holder");
        var assigned = (await api.AssignAsync(actor.Id, member.Id, "annual", api.Clock.GetUtcNow())).Value!;
        Assert.NotNull(assigned); Assert.Equal(0, await api.ScheduleAsync()); Assert.False(await api.DispatchAsync());
        await using (var context = Context())
        {
            Assert.Empty(await context.Notifications.AsNoTracking().ToArrayAsync(Token));
            Assert.Single(await context.Audit.AsNoTracking().ToArrayAsync(Token));
        }
        api.Options.Enabled = true;
        var renewed = (await api.AssignAsync(actor.Id, member.Id, "annual", assigned.ExpiresUtc, assigned.Version)).Value!;
        api.Options.Enabled = false;
        await using (var context = Context())
        {
            var pending = await context.Notifications.AsNoTracking().SingleAsync(Token);
            Assert.Equal("pending", pending.Status); Assert.Equal(0, pending.Attempts);
        }
        api.Clock.Advance(renewed.ExpiresUtc!.Value - api.Clock.GetUtcNow() - TimeSpan.FromDays(7));
        Assert.Equal(0, await api.ScheduleAsync()); Assert.Equal(0, await api.ScheduleAsync()); Assert.False(await api.DispatchAsync());
        Assert.Empty(api.Sender.AttemptedIds); Assert.Empty(api.Sender.Messages);
        await using var untouched = Context();
        var row = await untouched.Notifications.AsNoTracking().SingleAsync(Token);
        Assert.Equal("pending", row.Status); Assert.Equal(0, row.Attempts); Assert.Null(row.LeaseOwner);
    }

    [Fact]
    public async Task TransportUnavailableDoesNotScheduleOrConsumeDurableDeliveryIntent()
    {
        await database.ResetAsync(Token); await using var api = new SubscriptionNotificationTestHost(database);
        var actor = await api.AccountAsync("unavailable-notice-manager", manager: true);
        var member = await api.AccountAsync("unavailable-notice-holder");
        var assigned = (await api.AssignAsync(actor.Id, member.Id, "annual", api.Clock.GetUtcNow())).Value!;
        api.Sender.IsAvailable = false;
        api.Clock.Advance(assigned.ExpiresUtc!.Value - api.Clock.GetUtcNow() - TimeSpan.FromDays(7));
        Assert.False(await api.DispatchAsync());
        Assert.Empty(api.Sender.AttemptedIds);
        await using var context = Context(); var row = await context.Notifications.AsNoTracking().SingleAsync(Token);
        Assert.Equal("pending", row.Status); Assert.Equal(0, row.Attempts); Assert.Null(row.LeaseOwner);
    }

    [Fact]
    public async Task UnavailableGlobalEmailStillAllowsAssignmentWithoutCreatingMailIntent()
    {
        await database.ResetAsync(Token); await using var api = new SubscriptionNotificationTestHost(database, available: false);
        var actor = await api.AccountAsync("global-off-notice-manager", manager: true);
        var member = await api.AccountAsync("global-off-notice-holder");
        Assert.True((await api.AssignAsync(actor.Id, member.Id, "annual", api.Clock.GetUtcNow())).Succeeded);
        Assert.Equal(0, await api.ScheduleAsync()); Assert.False(await api.DispatchAsync()); Assert.Empty(api.Sender.AttemptedIds);
        await using var context = Context();
        Assert.Single(await context.Subscriptions.AsNoTracking().ToArrayAsync(Token));
        Assert.Single(await context.Audit.AsNoTracking().ToArrayAsync(Token)); Assert.Empty(await context.Notifications.AsNoTracking().ToArrayAsync(Token));
    }

    [Fact]
    public async Task ConcurrentAdministrativeAssignmentsCommitOneStateAuditAndNotification()
    {
        await database.ResetAsync(Token); await using var api = new SubscriptionNotificationTestHost(database);
        var actor = await api.AccountAsync("concurrent-notice-manager", manager: true);
        var member = await api.AccountAsync("concurrent-notice-holder");
        var now = api.Clock.GetUtcNow();
        var results = await Task.WhenAll(api.AssignAsync(actor.Id, member.Id, "annual", now), api.AssignAsync(actor.Id, member.Id, "probationismo", now));
        var winner = Assert.Single(results, result => result.Succeeded).Value!;
        Assert.Equal("concurrency_conflict", Assert.Single(results, result => !result.Succeeded).Error);
        await using var context = Context();
        var subscription = await context.Subscriptions.AsNoTracking().SingleAsync(Token);
        var audit = await context.Audit.AsNoTracking().SingleAsync(Token);
        var intent = await context.Notifications.AsNoTracking().SingleAsync(Token);
        Assert.Equal(winner.Version, subscription.Version); Assert.Equal(subscription.Id, audit.SubscriptionId);
        Assert.Equal(audit.Id, intent.SourceAuditId); Assert.Equal(subscription.Id, intent.SubscriptionId);
        Assert.Equal(subscription.UserId, intent.UserId); Assert.Equal(subscription.Plan, intent.Plan);
        Assert.Equal("assigned", intent.Kind); Assert.Equal("pending", intent.Status); Assert.Equal(0, intent.Attempts);
        Assert.Empty(api.Sender.Messages);
        Assert.True(await api.DispatchAsync());
        Assert.Equal(member.Email, Assert.Single(api.Sender.Messages).Recipient);
    }

    [Fact]
    public async Task GenuineRenewalHasOneDistinctMessageWhileNoopsAndStaleVersionsDoNotQueue()
    {
        await database.ResetAsync(Token); await using var api = new SubscriptionNotificationTestHost(database);
        var actor = await api.AccountAsync("renewed-notice-manager", manager: true);
        var member = await api.AccountAsync("renewed-notice-holder");
        var now = api.Clock.GetUtcNow();
        var assigned = (await api.AssignAsync(actor.Id, member.Id, "annual", now)).Value!;
        Assert.True(await api.DispatchAsync());
        var first = Assert.Single(api.Sender.Messages);
        Assert.Equal(member.Email, first.Recipient); Assert.Contains("asignado", first.Subject, StringComparison.OrdinalIgnoreCase);
        var oldEnd = assigned.ExpiresUtc!.Value;
        var renewed = (await api.AssignAsync(actor.Id, member.Id, "annual", oldEnd, assigned.Version)).Value!;
        Assert.Equal("concurrency_conflict", (await api.AssignAsync(actor.Id, member.Id, "annual", oldEnd, assigned.Version)).Error);
        Assert.Equal(renewed.Version, (await api.AssignAsync(actor.Id, member.Id, "annual", now, renewed.Version)).Value!.Version);
        Assert.Equal(renewed.Version, (await api.AssignAsync(actor.Id, member.Id, "annual", oldEnd, renewed.Version)).Value!.Version);
        api.Clock.Advance(TimeSpan.FromSeconds(15)); Assert.True(await api.DispatchAsync());
        var messages = api.Sender.Messages.ToArray(); Assert.Equal(2, messages.Length);
        Assert.NotEqual(first.MessageId, messages[1].MessageId); Assert.Equal(member.Email, messages[1].Recipient);
        Assert.NotEqual(first.Subject, messages[1].Subject);
        Assert.Contains("https://notification-qa.example.test", messages[1].Text, StringComparison.Ordinal);
        Assert.DoesNotContain("token=", messages[1].Text, StringComparison.OrdinalIgnoreCase);
        await using var context = Context();
        Assert.Equal(2, await context.Audit.CountAsync(Token));
        var intents = await context.Notifications.AsNoTracking().ToArrayAsync(Token);
        Assert.Equal(2, intents.Length); Assert.All(intents, intent => Assert.Equal("sent", intent.Status));
        Assert.Single(intents, intent => intent.Kind == "assigned"); Assert.Single(intents, intent => intent.Kind == "renewed");
        Assert.Equal(renewed.ExpiresUtc, Assert.Single(intents, intent => intent.Kind == "renewed").ExpiresUtc);
    }

    [Fact]
    public async Task FreeManualAssignmentSendsOnceWithoutExpiryButPersonalFreeActivationDoesNotQueue()
    {
        await database.ResetAsync(Token); await using var api = new SubscriptionNotificationTestHost(database);
        var actor = await api.AccountAsync("free-notice-manager", manager: true);
        var member = await api.AccountAsync("free-notice-holder"); var personal = await api.AccountAsync("self-free-notice-holder");
        var assigned = (await api.AssignAsync(actor.Id, member.Id, "free_beta", null)).Value!;
        Assert.Equal(assigned.Version, (await api.AssignAsync(actor.Id, member.Id, "free_beta", null, assigned.Version)).Value!.Version);
        await using (var scope = api.Services.CreateAsyncScope())
            Assert.True((await scope.ServiceProvider.GetRequiredService<ISubscriptionService>().ActivateAsync(personal.Id, Token)).Succeeded);
        Assert.Equal(0, await api.ScheduleAsync()); Assert.True(await api.DispatchAsync());
        Assert.Equal(member.Email, Assert.Single(api.Sender.Messages).Recipient);
        await using var context = Context();
        var intent = await context.Notifications.AsNoTracking().SingleAsync(Token);
        Assert.Equal("assigned", intent.Kind); Assert.Equal("free_beta", intent.Plan); Assert.Null(intent.ExpiresUtc);
        Assert.Equal(2, await context.Subscriptions.CountAsync(Token)); Assert.Equal(2, await context.Audit.CountAsync(Token));
    }

    [Fact]
    public async Task OutboxInsertFailureRollsBackSubscriptionAuditAndVersionBeforeAnExplicitRetry()
    {
        await database.ResetAsync(Token); await using var api = new SubscriptionNotificationTestHost(database);
        var actor = await api.AccountAsync("rollback-notice-manager", manager: true);
        var member = await api.AccountAsync("rollback-notice-holder");
        var before = (await api.AssignAsync(actor.Id, member.Id, "free_beta", null)).Value!;
        api.Clock.Advance(TimeSpan.FromSeconds(1)); var now = api.Clock.GetUtcNow();
        const string cleanup = "DROP TRIGGER IF EXISTS qa_notification_insert_failure ON subscriptions.\"NotificationOutbox\"; DROP FUNCTION IF EXISTS subscriptions.qa_notification_insert_failure();";
        try
        {
            await database.ExecuteAsync("""
                CREATE FUNCTION subscriptions.qa_notification_insert_failure() RETURNS trigger LANGUAGE plpgsql AS $qa$
                BEGIN RAISE EXCEPTION 'Synthetic outbox insert failure' USING ERRCODE='23514'; END; $qa$;
                CREATE TRIGGER qa_notification_insert_failure AFTER INSERT ON subscriptions."NotificationOutbox"
                FOR EACH ROW EXECUTE FUNCTION subscriptions.qa_notification_insert_failure();
                """, Token);
            var error = await Assert.ThrowsAsync<DbUpdateException>(() => api.AssignAsync(actor.Id, member.Id, "annual", now, before.Version));
            Assert.Equal("23514", Assert.IsType<PostgresException>(error.InnerException).SqlState);
            await using var context = Context(); var unchanged = await context.Subscriptions.AsNoTracking().SingleAsync(Token);
            Assert.Equal(before.Version, unchanged.Version); Assert.Equal("free_beta", unchanged.Plan); Assert.Null(unchanged.ExpiresUtc);
            Assert.Single(await context.Audit.AsNoTracking().ToArrayAsync(Token));
            Assert.Equal("free_beta", (await context.Notifications.AsNoTracking().SingleAsync(Token)).Plan);
        }
        finally { await database.ExecuteAsync(cleanup, CancellationToken.None); }
        Assert.True((await api.AssignAsync(actor.Id, member.Id, "annual", now, before.Version)).Succeeded);
        Assert.True(await api.DispatchAsync()); Assert.True(await api.DispatchAsync()); Assert.Single(api.Sender.Messages);
        await using var saved = Context(); Assert.Equal(2, await saved.Audit.CountAsync(Token));
        var intents = await saved.Notifications.AsNoTracking().ToArrayAsync(Token);
        Assert.Equal(2, intents.Length); Assert.Single(intents, intent => intent.Status == "cancelled"); Assert.Single(intents, intent => intent.Status == "sent");
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("unconfirmed")]
    [InlineData("revalidation")]
    public async Task RecipientRevocationAfterAssignmentCancelsDeliveryWithoutLeakingAStaleAddress(string state)
    {
        await database.ResetAsync(Token); await using var api = new SubscriptionNotificationTestHost(database);
        var actor = await api.AccountAsync("revoked-notice-manager", manager: true);
        var member = await api.AccountAsync("revoked-notice-holder");
        Assert.True((await api.AssignAsync(actor.Id, member.Id, "annual", api.Clock.GetUtcNow())).Succeeded);
        await using (var identity = database.Context(true))
        {
            var account = await identity.Users.SingleAsync(user => user.Id == member.Id, Token);
            if (state == "disabled") account.IsDisabled = true;
            if (state == "unconfirmed") account.EmailConfirmed = false;
            if (state == "revalidation") account.RevalidationRequired = true;
            await identity.SaveChangesAsync(Token);
        }
        Assert.True(await api.DispatchAsync()); Assert.Empty(api.Sender.AttemptedIds); Assert.Empty(api.Sender.Messages);
        await using var context = Context(); Assert.Equal("cancelled", (await context.Notifications.AsNoTracking().SingleAsync(Token)).Status);
    }

    [Fact]
    public async Task RecipientIsResolvedFromCurrentConfirmedIdentityRatherThanAnEmailSnapshotInTheOutbox()
    {
        await database.ResetAsync(Token); await using var api = new SubscriptionNotificationTestHost(database);
        var actor = await api.AccountAsync("changed-email-notice-manager", manager: true);
        var member = await api.AccountAsync("changed-email-notice-holder");
        Assert.True((await api.AssignAsync(actor.Id, member.Id, "annual", api.Clock.GetUtcNow())).Succeeded);
        const string current = "current-confirmed-holder@example.test";
        await using (var identity = database.Context(true))
        {
            var account = await identity.Users.SingleAsync(user => user.Id == member.Id, Token);
            account.Email = current; account.NormalizedEmail = current.ToUpperInvariant();
            account.UserName = current; account.NormalizedUserName = current.ToUpperInvariant(); account.EmailConfirmed = true;
            await identity.SaveChangesAsync(Token);
        }
        Assert.True(await api.DispatchAsync()); var message = Assert.Single(api.Sender.Messages);
        Assert.Equal(current, message.Recipient); Assert.DoesNotContain(member.Email!, message.Text, StringComparison.OrdinalIgnoreCase);
    }

    private SubscriptionsDbContext Context(bool migration = false)
    {
        var options = new DbContextOptionsBuilder<SubscriptionsDbContext>();
        SubscriptionsRegistration.ConfigureDatabase(options, migration ? database.MigrationConnection : database.RuntimeConnection);
        return new(options.Options);
    }
}
