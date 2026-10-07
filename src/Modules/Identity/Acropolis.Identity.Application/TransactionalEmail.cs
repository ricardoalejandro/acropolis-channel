namespace Acropolis.Identity.Application;

public sealed record NotificationRecipient(string Email);
public interface IAccountNotificationRecipient
{
    Task<NotificationRecipient?> GetAsync(Guid userId, CancellationToken token);
}
public sealed record TransactionalEmail(Guid MessageId, string Recipient, string Subject, string Text);
public interface ITransactionalEmailSender
{
    bool IsAvailable { get; }
    string PublicOrigin { get; }
    Task SendAsync(TransactionalEmail email, CancellationToken token);
}
