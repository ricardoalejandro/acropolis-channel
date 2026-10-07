using Acropolis.Identity.IntegrationTests;
using Acropolis.Subscriptions.Application;
using Acropolis.Subscriptions.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Acropolis.Subscriptions.IntegrationTests;

[Collection("IdentityPostgres")]
public sealed class SubscriptionNotificationSchedulerTests(IdentityFixture database)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SevenDayWindowIsInclusiveOnlyForActiveStartedFullTermsAndExclusiveAtExpiry()
    {
        await database.ResetAsync(Token); await using var api = new SubscriptionNotificationTestHost(database);
        var actor = await api.AccountAsync("window-notice-manager", manager: true);
        var now = api.Clock.GetUtcNow();
        var exact = await TermAsync(api, actor.Id, "window-exact", now.AddDays(7));
        var inside = await TermAsync(api, actor.Id, "window-inside", now.AddDays(1));
        var outside = await TermAsync(api, actor.Id, "window-outside", now.AddDays(7).AddTicks(10));
        var boundary = await TermAsync(api, actor.Id, "window-boundary", now);
        var expired = await TermAsync(api, actor.Id, "window-expired", now.AddSeconds(-1));
        var scheduledUser = await api.AccountAsync("window-scheduled");
        var scheduled = (await api.AssignAsync(actor.Id, scheduledUser.Id, "annual", now.AddDays(1))).Value!;
        var cancelled = await TermAsync(api, actor.Id, "window-cancelled", now.AddDays(1));
        Assert.True((await api.UpdateAsync(actor.Id, cancelled, "cancelled")).Succeeded);
        var suspended = await TermAsync(api, actor.Id, "window-suspended", now.AddDays(1));
        Assert.True((await api.UpdateAsync(actor.Id, suspended, "suspended")).Succeeded);
        var freeUser = await api.AccountAsync("window-free");
        var free = (await api.AssignAsync(actor.Id, freeUser.Id, "free_beta", null)).Value!;
        await CompleteAssignmentsAsync(); // Focus the independent scheduler outcome, not SMTP setup timing.
        Assert.Equal(2, await api.ScheduleAsync()); Assert.Equal(0, await api.ScheduleAsync());
        Assert.Empty(api.Sender.AttemptedIds); Assert.Empty(api.Sender.Messages);
        await using var context = Context();
        var reminders = await context.Notifications.AsNoTracking().Where(row => row.Kind == "expiring").ToArrayAsync(Token);
        Assert.Equal(2, reminders.Length);
        Assert.Equal(new[] { exact.Id, inside.Id }.Order(), reminders.Select(row => row.SubscriptionId).Order());
        foreach (var excluded in new[] { outside, boundary, expired, scheduled, cancelled, suspended, free })
            Assert.DoesNotContain(reminders, row => row.SubscriptionId == excluded.Id);
        Assert.All(reminders, row => { Assert.Equal("pending", row.Status); Assert.Equal(0, row.Attempts); });
    }

    [Fact]
    public async Task ConcurrentSchedulersCreateOneReminderPerConcreteTermWithoutAutomaticSending()
    {
        await database.ResetAsync(Token); await using var api = new SubscriptionNotificationTestHost(database);
        var actor = await api.AccountAsync("dedup-notice-manager", manager: true);
        var row = await TermAsync(api, actor.Id, "dedup-notice-holder", api.Clock.GetUtcNow().AddDays(7));
        await CompleteAssignmentsAsync();
        var counts = await Task.WhenAll(api.ScheduleAsync(), api.ScheduleAsync(), api.ScheduleAsync());
        Assert.Equal(1, counts.Sum());
        await using var context = Context(); var reminder = await context.Notifications.AsNoTracking().SingleAsync(item => item.Kind == "expiring", Token);
        Assert.Equal(row.Id, reminder.SubscriptionId); Assert.Equal(row.StartsUtc, reminder.StartsUtc); Assert.Equal(row.ExpiresUtc, reminder.ExpiresUtc);
        Assert.Equal(0, reminder.Attempts); Assert.Equal("pending", reminder.Status); Assert.Empty(api.Sender.Messages);
        Assert.True(await api.DispatchAsync()); Assert.Single(api.Sender.Messages);
        api.Clock.Advance(TimeSpan.FromDays(1)); Assert.Equal(0, await api.ScheduleAsync()); Assert.False(await api.DispatchAsync());
    }

    [Fact]
    public async Task ExtensionCancelsAnOldReminderAndAllowsOneNewReminderForTheGenuineExtendedTerm()
    {
        await database.ResetAsync(Token); await using var api = new SubscriptionNotificationTestHost(database);
        var actor = await api.AccountAsync("extended-notice-manager", manager: true);
        var original = await TermAsync(api, actor.Id, "extended-notice-holder", api.Clock.GetUtcNow().AddDays(7));
        await CompleteAssignmentsAsync(); Assert.Equal(1, await api.ScheduleAsync());
        Guid originalReminder; Guid generation;
        await using (var context = Context())
        {
            var intent = await context.Notifications.AsNoTracking().SingleAsync(item => item.Kind == "expiring", Token);
            originalReminder = intent.Id; generation = intent.TermGeneration;
        }
        api.Clock.Advance(TimeSpan.FromSeconds(1));
        var renewed = (await api.AssignAsync(actor.Id, original.UserId, "annual", original.ExpiresUtc, original.Version)).Value!;
        // The old reminder is selected first by its earlier due time; it must never reach SMTP.
        Assert.True(await api.DispatchAsync());
        Assert.DoesNotContain(api.Sender.Messages, message => message.MessageId == originalReminder);
        await using (var context = Context())
            Assert.Equal("cancelled", (await context.Notifications.AsNoTracking().SingleAsync(item => item.Id == originalReminder, Token)).Status);
        await CompleteAssignmentsAsync();
        api.Clock.Advance(renewed.ExpiresUtc!.Value - api.Clock.GetUtcNow() - TimeSpan.FromDays(7));
        Assert.Equal(1, await api.ScheduleAsync()); Assert.Equal(0, await api.ScheduleAsync());
        await using var check = Context();
        var reminders = await check.Notifications.AsNoTracking().Where(item => item.Kind == "expiring").ToArrayAsync(Token);
        Assert.Equal(2, reminders.Length); var current = Assert.Single(reminders, item => item.Status == "pending");
        Assert.NotEqual(generation, current.TermGeneration); Assert.NotEqual(originalReminder, current.Id);
        Assert.Equal(renewed.StartsUtc, current.StartsUtc); Assert.Equal(renewed.ExpiresUtc, current.ExpiresUtc);
    }

    [Theory]
    [InlineData("cancelled")]
    [InlineData("suspended")]
    [InlineData("expired")]
    [InlineData("scheduled")]
    public async Task PendingRemindersRecheckTermsAndStateImmediatelyBeforeTransport(string change)
    {
        await database.ResetAsync(Token); await using var api = new SubscriptionNotificationTestHost(database);
        var actor = await api.AccountAsync("obsolete-notice-manager", manager: true);
        var row = await TermAsync(api, actor.Id, "obsolete-notice-holder", api.Clock.GetUtcNow().AddDays(7));
        await CompleteAssignmentsAsync(); Assert.Equal(1, await api.ScheduleAsync());
        if (change is "cancelled" or "suspended") Assert.True((await api.UpdateAsync(actor.Id, row, change)).Succeeded);
        else if (change == "expired") api.Clock.Advance(TimeSpan.FromDays(7));
        else
        {
            await using var maintenance = Context(true);
            await maintenance.Subscriptions.Where(item => item.Id == row.Id).ExecuteUpdateAsync(setters =>
                setters.SetProperty(item => item.StartsUtc, api.Clock.GetUtcNow().AddDays(1))
                    .SetProperty(item => item.ExpiresUtc, api.Clock.GetUtcNow().AddYears(1)), Token);
        }
        Assert.True(await api.DispatchAsync()); Assert.Empty(api.Sender.AttemptedIds); Assert.Empty(api.Sender.Messages);
        await using var context = Context();
        Assert.Equal("cancelled", (await context.Notifications.AsNoTracking().SingleAsync(item => item.Kind == "expiring", Token)).Status);
    }

    private async Task<SubscriptionView> TermAsync(SubscriptionNotificationTestHost api, Guid actor, string suffix, DateTimeOffset expires)
    {
        var member = await api.AccountAsync(suffix);
        var result = await api.AssignAsync(actor, member.Id, "annual", expires.AddYears(-1));
        Assert.True(result.Succeeded); Assert.Equal(expires, result.Value!.ExpiresUtc); return result.Value;
    }
    private async Task CompleteAssignmentsAsync()
    {
        await using var context = Context(true);
        await context.Notifications.Where(item => item.Kind != "expiring").ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Status, "sent"), Token);
    }
    private SubscriptionsDbContext Context(bool migration = false)
    {
        var options = new DbContextOptionsBuilder<SubscriptionsDbContext>();
        SubscriptionsRegistration.ConfigureDatabase(options, migration ? database.MigrationConnection : database.RuntimeConnection);
        return new(options.Options);
    }
}
