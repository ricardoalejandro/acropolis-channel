namespace Acropolis.Subscriptions.Application;

public sealed record SubscriptionView(Guid Id, Guid UserId, string Plan, string Status, DateTimeOffset CreatedUtc, DateTimeOffset ActivatedUtc, DateTimeOffset UpdatedUtc, DateTimeOffset? CancelledUtc, DateTimeOffset? ExpiresUtc, string Version);
public sealed record SubscriptionStatusView(SubscriptionView? Subscription, bool EligibleToActivate);
public sealed record SubscriptionAccountView(Guid Id, string DisplayName, string Email, string Status, bool EmailConfirmed);
public sealed record SubscriptionAccountPage(SubscriptionAccountView[] Items, int Total, int Page, int PageSize);
public sealed record SubscriptionPage(SubscriptionView[] Items, int Total, int Page, int PageSize);
public sealed record ActivateSubscriptionRequest;
public sealed record CancelSubscriptionRequest(string Version);
public sealed record AdminSubscriptionRequest(string Version, string Status, string Reason);
public sealed record SubscriptionAuditView(Guid Id, Guid SubscriptionId, Guid UserId, Guid ActorId, string Action, string? BeforeStatus, string AfterStatus, string Reason, DateTimeOffset CreatedUtc);
public sealed record SubscriptionAuditPage(SubscriptionAuditView[] Items, int Total, int Page, int PageSize);
public interface ISubscriptionAccess
{
    Task<bool> HasActiveAsync(Guid userId, CancellationToken token);
}
public interface ISubscriptionService
{
    Task<SubscriptionStatusView> GetMineAsync(Guid userId, CancellationToken token);
    Task<SubscriptionResult<SubscriptionView>> ActivateAsync(Guid userId, CancellationToken token);
    Task<SubscriptionResult<SubscriptionView>> CancelAsync(Guid userId, CancelSubscriptionRequest request, CancellationToken token);
    Task<SubscriptionPage> ListAsync(string? status, Guid? userId, int page, int pageSize, CancellationToken token);
    Task<SubscriptionView?> GetAsync(Guid id, CancellationToken token);
    Task<SubscriptionResult<SubscriptionView>> UpdateAsync(Guid actorId, Guid id, AdminSubscriptionRequest request, CancellationToken token);
    Task<SubscriptionAuditPage> ListAuditAsync(DateTimeOffset? fromUtc, DateTimeOffset? toUtc, string? action, Guid? subscriptionId, Guid? userId, int page, int pageSize, CancellationToken token);
}
