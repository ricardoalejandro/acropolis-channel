namespace Acropolis.Subscriptions.Application;

public static class SubscriptionRules
{
    public const string Plan = "free_beta";
    public static IReadOnlyList<string> Statuses { get; } = Array.AsReadOnly(new[] { "active", "cancelled", "suspended" });
    public static IReadOnlyList<string> AuditActions { get; } = Array.AsReadOnly(new[]
    {
        "subscription.activated", "subscription.reactivated", "subscription.cancelled", "subscription.updated", "subscription.recovery_suspended"
    });
    public static bool ValidPage(int page, int pageSize) => page is >= 1 and <= 1000000 && pageSize is >= 1 and <= 100;
    public static bool ValidVersion(string? version) => !string.IsNullOrWhiteSpace(version) && version.Length <= 64 && !version.Any(char.IsControl);
    public static bool ValidReason(string? reason) => reason?.Trim().Length is >= 2 and <= 200 && !reason.Any(char.IsControl);
    public static bool ValidAdmin(AdminSubscriptionRequest request) => ValidVersion(request.Version) && Statuses.Contains(request.Status) && ValidReason(request.Reason);
    public static bool ValidAudit(DateTimeOffset? fromUtc, DateTimeOffset? toUtc, string? action, int page, int pageSize) =>
        ValidPage(page, pageSize) && (fromUtc is null || toUtc is null || fromUtc <= toUtc) && (action is null || AuditActions.Contains(action));
    public static bool Eligible(bool confirmedActive, string? status) => confirmedActive && (status is null or "cancelled");
}
