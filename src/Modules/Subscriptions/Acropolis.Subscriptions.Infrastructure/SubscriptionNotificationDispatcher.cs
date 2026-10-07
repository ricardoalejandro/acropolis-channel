using Acropolis.Identity.Application;
using Acropolis.Subscriptions.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Acropolis.Subscriptions.Infrastructure;

public sealed class SubscriptionNotificationDispatcher(SubscriptionsDbContext database, IOptions<SubscriptionNotificationOptions> settings,
    IAccountNotificationRecipient recipients, ITransactionalEmailSender sender, TimeProvider clock)
{
    public async Task<bool> DispatchAsync(CancellationToken token)
    {
        if (!settings.Value.Enabled || !sender.IsAvailable) return false;
        var now = clock.GetUtcNow();
        var owner = Guid.NewGuid().ToString("N");
        SubscriptionNotification message;
        NotificationRecipient? recipient;
        await using (var transaction = await database.Database.BeginTransactionAsync(token))
        {
            var budget = await database.NotificationDeliveryState.FromSqlRaw("SELECT * FROM subscriptions.\"NotificationDeliveryState\" WHERE \"Id\"=1 FOR UPDATE SKIP LOCKED").AsNoTracking().SingleOrDefaultAsync(token);
            if (budget is null || budget.NextSubmissionUtc > now) return false;
            var found = await database.Notifications.FromSqlInterpolated($"""
                SELECT * FROM subscriptions."NotificationOutbox"
                WHERE ("Status"='pending' AND "NextAttemptUtc"<={now}) OR ("Status"='sending' AND "LeaseExpiresUtc"<={now})
                ORDER BY "NextAttemptUtc", "Id" LIMIT 1 FOR UPDATE SKIP LOCKED
                """).AsNoTracking().SingleOrDefaultAsync(token);
            if (found is null) return false;
            message = found;
            var row = await database.Subscriptions.FromSqlInterpolated($"SELECT * FROM subscriptions.\"Subscriptions\" WHERE \"Id\"={message.SubscriptionId} FOR SHARE").AsNoTracking().SingleOrDefaultAsync(token);
            recipient = row is not null && Current(message, row, now) ? await recipients.GetAsync(message.UserId, token) : null;
            if (recipient is null || message.Attempts >= SubscriptionNotificationRules.MaximumAttempts)
            {
                var terminal = message.Attempts >= SubscriptionNotificationRules.MaximumAttempts ? "failed" : "cancelled";
                await database.Notifications.Where(x => x.Id == message.Id).ExecuteUpdateAsync(setters =>
                    setters.SetProperty(x => x.Status, terminal).SetProperty(x => x.LeaseOwner, (string?)null)
                        .SetProperty(x => x.LeaseExpiresUtc, (DateTimeOffset?)null), token);
                await transaction.CommitAsync(token);
                return true;
            }
            var leaseUntil = now.Add(SubscriptionNotificationRules.LeaseLifetime);
            var nextSubmission = now.Add(SubscriptionNotificationRules.SubmissionSpacing);
            await database.Notifications.Where(x => x.Id == message.Id).ExecuteUpdateAsync(setters =>
                setters.SetProperty(x => x.Status, "sending").SetProperty(x => x.LeaseOwner, owner)
                    .SetProperty(x => x.LeaseExpiresUtc, leaseUntil).SetProperty(x => x.Attempts, x => x.Attempts + 1), token);
            await database.NotificationDeliveryState.Where(x => x.Id == 1)
                .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.NextSubmissionUtc, nextSubmission), token);
            message.Attempts++;
            await transaction.CommitAsync(token);
        }
        var delivered = false;
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            deadline.CancelAfter(SubscriptionNotificationRules.SendDeadline);
            try
            {
                var notice = SubscriptionNotificationMessages.Create(message.Kind, message.Plan, message.StartsUtc, message.ExpiresUtc, sender.PublicOrigin);
                await sender.SendAsync(new(message.Id, recipient!.Email, notice.Subject, notice.Text), deadline.Token);
                delivered = true;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception) { /* No recipient, body, credentials or provider details are logged. */ }
        }
        var status = delivered ? "sent" : message.Attempts >= SubscriptionNotificationRules.MaximumAttempts ? "failed" : "pending";
        var next = clock.GetUtcNow().Add(SubscriptionNotificationRules.RetryDelay(message.Attempts));
        await database.Notifications.Where(x => x.Id == message.Id && x.Status == "sending" && x.LeaseOwner == owner)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.Status, status)
                .SetProperty(x => x.LeaseOwner, (string?)null).SetProperty(x => x.LeaseExpiresUtc, (DateTimeOffset?)null)
                .SetProperty(x => x.NextAttemptUtc, next), token);
        return true;
    }
    private static bool Current(SubscriptionNotification notice, Subscription row, DateTimeOffset now) => row.Status == "active" &&
        row.UserId == notice.UserId && row.NotificationTermGeneration == notice.TermGeneration && row.Plan == notice.Plan &&
        row.StartsUtc == notice.StartsUtc && row.ExpiresUtc == notice.ExpiresUtc && (row.ExpiresUtc is null || now < row.ExpiresUtc) &&
        (notice.Kind != "expiring" || SubscriptionNotificationRules.ReminderDue(row.Status, row.Plan, row.StartsUtc, row.ExpiresUtc, now));
}
