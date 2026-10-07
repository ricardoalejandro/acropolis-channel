namespace Acropolis.Subscriptions.Application;

public sealed record SubscriptionReportCount(string Key, long Count);
public sealed record SubscriptionReportView(DateTimeOffset GeneratedUtc, long Total,
    SubscriptionReportCount[] ByStatus, SubscriptionReportCount[]? ByEffectiveState = null)
{
    public string Scope => "current";
}
public interface ISubscriptionReportService
{
    Task<SubscriptionReportView> GetCurrentAsync(CancellationToken token);
}
