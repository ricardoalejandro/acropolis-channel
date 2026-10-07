using Acropolis.Identity.IntegrationTests;
using Acropolis.Subscriptions.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Acropolis.Subscriptions.IntegrationTests;

[Collection("IdentityPostgres")]
public sealed class SubscriptionNotificationLeaseTests(IdentityFixture database)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ConcurrentDispatchersShareTheDatabaseBudgetAndNeverSubmitCloserThanFifteenSeconds()
    {
        await database.ResetAsync(Token); await using var api = new SubscriptionNotificationTestHost(database);
        var actor = await api.AccountAsync("budget-notice-manager", manager: true);
        for (var index = 0; index < 3; index++)
        {
            var member = await api.AccountAsync("budget-notice-holder-" + index);
            Assert.True((await api.AssignAsync(actor.Id, member.Id, "annual", api.Clock.GetUtcNow())).Succeeded);
        }
        var results = await Task.WhenAll(api.DispatchAsync(), api.DispatchAsync(), api.DispatchAsync());
        Assert.Equal(1, results.Count(result => result)); Assert.Single(api.Sender.Messages);
        await using (var context = Context())
        {
            var rows = await context.Notifications.AsNoTracking().ToArrayAsync(Token);
            Assert.Single(rows, row => row.Status == "sent");
            Assert.All(rows.Where(row => row.Status == "pending"), row => { Assert.Equal(0, row.Attempts); Assert.Null(row.LeaseOwner); });
        }
        api.Clock.Advance(TimeSpan.FromSeconds(15) - TimeSpan.FromTicks(10)); Assert.False(await api.DispatchAsync()); Assert.Single(api.Sender.Messages);
        api.Clock.Advance(TimeSpan.FromTicks(10)); Assert.True(await api.DispatchAsync()); Assert.Equal(2, api.Sender.Messages.Count);
        Assert.False(await api.DispatchAsync());
        api.Clock.Advance(TimeSpan.FromSeconds(15)); Assert.True(await api.DispatchAsync()); Assert.Equal(3, api.Sender.Messages.Count);
        Assert.Equal(3, api.Sender.Messages.Select(message => message.MessageId).Distinct().Count());
        Assert.False(await api.DispatchAsync());
    }

    [Fact]
    public async Task ReusedDispatcherAfterNoWorkAndCancellationStillReadsAnotherWorkersFreshSubmissionBudget()
    {
        await database.ResetAsync(Token); await using var api = new SubscriptionNotificationTestHost(database);
        var actor = await api.AccountAsync("reused-notice-manager", manager: true);
        var first = await api.AccountAsync("reused-notice-first"); var second = await api.AccountAsync("reused-notice-second");
        await using var longLivedScope = api.Services.CreateAsyncScope();
        var dispatcher = longLivedScope.ServiceProvider.GetRequiredService<SubscriptionNotificationDispatcher>();
        Assert.False(await dispatcher.DispatchAsync(Token)); // A prior read with no work must not pin budget state.
        var free = (await api.AssignAsync(actor.Id, first.Id, "free_beta", null)).Value!;
        api.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True((await api.AssignAsync(actor.Id, first.Id, "annual", api.Clock.GetUtcNow(), free.Version)).Succeeded);
        Assert.True(await dispatcher.DispatchAsync(Token)); // Cancels the obsolete free notice in this same scope.
        Assert.Empty(api.Sender.AttemptedIds);
        Assert.True(await api.DispatchAsync()); // Another independent worker consumes the real database budget.
        Assert.Equal(first.Email, Assert.Single(api.Sender.Messages).Recipient);
        Assert.True((await api.AssignAsync(actor.Id, second.Id, "annual", api.Clock.GetUtcNow())).Succeeded);
        Assert.False(await dispatcher.DispatchAsync(Token)); Assert.Single(api.Sender.Messages);
        await using (var context = Context())
        {
            var untouched = await context.Notifications.AsNoTracking().SingleAsync(row => row.UserId == second.Id, Token);
            Assert.Equal("pending", untouched.Status); Assert.Equal(0, untouched.Attempts);
        }
        api.Clock.Advance(TimeSpan.FromSeconds(15)); Assert.True(await dispatcher.DispatchAsync(Token));
        var delivered = api.Sender.Messages.ToArray(); Assert.Equal(2, delivered.Length); Assert.Equal(second.Email, delivered[1].Recipient);
        Assert.NotEqual(delivered[0].MessageId, delivered[1].MessageId);
    }

    [Fact]
    public async Task LiveLeaseCannotBeStolenAndExpiredLeaseRecoversTheSameStableMessageId()
    {
        await database.ResetAsync(Token); await using var api = new SubscriptionNotificationTestHost(database);
        var actor = await api.AccountAsync("lease-notice-manager", manager: true);
        var member = await api.AccountAsync("lease-notice-holder");
        Assert.True((await api.AssignAsync(actor.Id, member.Id, "annual", api.Clock.GetUtcNow())).Succeeded);
        Guid id;
        await using (var setup = Context(true))
        {
            var row = await setup.Notifications.SingleAsync(Token); id = row.Id;
            row.Status = "sending"; row.Attempts = 1; row.LeaseOwner = "synthetic_previous_worker";
            row.LeaseExpiresUtc = api.Clock.GetUtcNow().AddSeconds(30); await setup.SaveChangesAsync(Token);
        }
        Assert.False(await api.DispatchAsync()); Assert.Empty(api.Sender.AttemptedIds);
        api.Clock.Advance(TimeSpan.FromSeconds(30) - TimeSpan.FromTicks(10)); Assert.False(await api.DispatchAsync());
        api.Clock.Advance(TimeSpan.FromTicks(10)); Assert.True(await api.DispatchAsync());
        Assert.Equal(id, Assert.Single(api.Sender.Messages).MessageId);
        await using var context = Context(); var saved = await context.Notifications.AsNoTracking().SingleAsync(Token);
        Assert.Equal("sent", saved.Status); Assert.Equal(2, saved.Attempts); Assert.Null(saved.LeaseOwner); Assert.Null(saved.LeaseExpiresUtc);
    }

    [Fact]
    public async Task FailuresBackOffStopAtFiveAttemptsAndKeepTheSameIdAcrossExplicitRecovery()
    {
        await database.ResetAsync(Token); await using var api = new SubscriptionNotificationTestHost(database);
        var actor = await api.AccountAsync("retry-notice-manager", manager: true);
        var member = await api.AccountAsync("retry-notice-holder");
        Assert.True((await api.AssignAsync(actor.Id, member.Id, "annual", api.Clock.GetUtcNow())).Succeeded);
        api.Sender.Fail = true; Guid? stable = null;
        foreach (var expectedDelay in new[] { 30, 60, 120, 240, 480 })
        {
            var before = api.Clock.GetUtcNow(); Assert.True(await api.DispatchAsync());
            await using var context = Context(); var row = await context.Notifications.AsNoTracking().SingleAsync(Token);
            stable ??= row.Id; Assert.Equal(stable, row.Id);
            Assert.Equal(api.Sender.AttemptedIds.Count, row.Attempts);
            Assert.Equal(row.Attempts == 5 ? "failed" : "pending", row.Status);
            Assert.Null(row.LeaseOwner); Assert.Null(row.LeaseExpiresUtc);
            if (row.Attempts < 5)
            {
                Assert.Equal(before.AddSeconds(expectedDelay), row.NextAttemptUtc);
                api.Clock.Advance(TimeSpan.FromSeconds(expectedDelay) - TimeSpan.FromTicks(10));
                Assert.False(await api.DispatchAsync());
                api.Clock.Advance(TimeSpan.FromTicks(10));
            }
        }
        Assert.Empty(api.Sender.Messages); Assert.Equal(5, api.Sender.AttemptedIds.Count);
        Assert.Single(api.Sender.AttemptedIds.Distinct());
        api.Sender.Fail = false; api.Clock.Advance(TimeSpan.FromDays(1));
        Assert.False(await api.DispatchAsync()); Assert.Empty(api.Sender.Messages); Assert.Equal(5, api.Sender.AttemptedIds.Count);
    }

    [Fact]
    public async Task AcceptedRemoteDeliveryFollowedByDatabaseFailureCanRepeatButNeverChangesMessageIdentity()
    {
        await database.ResetAsync(Token); await using var api = new SubscriptionNotificationTestHost(database);
        var actor = await api.AccountAsync("atleast-once-notice-manager", manager: true);
        var member = await api.AccountAsync("atleast-once-notice-holder");
        Assert.True((await api.AssignAsync(actor.Id, member.Id, "annual", api.Clock.GetUtcNow())).Succeeded);
        const string cleanup = "DROP TRIGGER IF EXISTS qa_notification_ack_failure ON subscriptions.\"NotificationOutbox\"; DROP FUNCTION IF EXISTS subscriptions.qa_notification_ack_failure();";
        try
        {
            await database.ExecuteAsync("""
                CREATE FUNCTION subscriptions.qa_notification_ack_failure() RETURNS trigger LANGUAGE plpgsql AS $qa$
                BEGIN RAISE EXCEPTION 'Synthetic local acknowledgement failure' USING ERRCODE='23514'; END; $qa$;
                CREATE TRIGGER qa_notification_ack_failure BEFORE UPDATE OF "Status" ON subscriptions."NotificationOutbox"
                FOR EACH ROW WHEN (OLD."Status"='sending' AND NEW."Status"='sent')
                EXECUTE FUNCTION subscriptions.qa_notification_ack_failure();
                """, Token);
            var error = await Assert.ThrowsAsync<PostgresException>(() => api.DispatchAsync());
            Assert.Equal("23514", error.SqlState); Assert.Single(api.Sender.Messages);
            await using var context = Context(); var row = await context.Notifications.AsNoTracking().SingleAsync(Token);
            Assert.Equal("sending", row.Status); Assert.Equal(1, row.Attempts); Assert.NotNull(row.LeaseOwner);
        }
        finally { await database.ExecuteAsync(cleanup, CancellationToken.None); }
        api.Clock.Advance(TimeSpan.FromSeconds(31)); Assert.True(await api.DispatchAsync());
        var deliveries = api.Sender.Messages.ToArray(); Assert.Equal(2, deliveries.Length);
        Assert.Equal(deliveries[0].MessageId, deliveries[1].MessageId); Assert.Equal(deliveries[0].Text, deliveries[1].Text);
        await using var saved = Context(); Assert.Equal("sent", (await saved.Notifications.AsNoTracking().SingleAsync(Token)).Status);
    }

    [Fact]
    public async Task ADisplacedWorkerCannotOverwriteTheNewLeaseOwnerAfterItsTransportReturns()
    {
        await database.ResetAsync(Token); await using var api = new SubscriptionNotificationTestHost(database);
        var actor = await api.AccountAsync("displaced-notice-manager", manager: true);
        var member = await api.AccountAsync("displaced-notice-holder");
        Assert.True((await api.AssignAsync(actor.Id, member.Id, "annual", api.Clock.GetUtcNow())).Succeeded);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        api.Sender.BeforeSend = async (_, token) => { entered.TrySetResult(true); await release.Task.WaitAsync(token); };
        var sending = api.DispatchAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            await using var other = Context(true);
            await other.Notifications.Where(row => row.Status == "sending").ExecuteUpdateAsync(setters =>
                setters.SetProperty(row => row.LeaseOwner, "synthetic_replacement_worker")
                    .SetProperty(row => row.LeaseExpiresUtc, api.Clock.GetUtcNow().AddMinutes(1)), Token);
        }
        finally { release.TrySetResult(true); }
        Assert.True(await sending); Assert.Single(api.Sender.Messages);
        await using var context = Context(); var preserved = await context.Notifications.AsNoTracking().SingleAsync(Token);
        Assert.Equal("sending", preserved.Status); Assert.Equal("synthetic_replacement_worker", preserved.LeaseOwner);
        Assert.Equal(1, preserved.Attempts);
    }

    private SubscriptionsDbContext Context(bool migration = false)
    {
        var options = new DbContextOptionsBuilder<SubscriptionsDbContext>();
        SubscriptionsRegistration.ConfigureDatabase(options, migration ? database.MigrationConnection : database.RuntimeConnection);
        return new(options.Options);
    }
}
