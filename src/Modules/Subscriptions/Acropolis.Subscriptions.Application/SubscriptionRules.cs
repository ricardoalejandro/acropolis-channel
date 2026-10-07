namespace Acropolis.Subscriptions.Application;

public static class SubscriptionRules
{
    public const string Plan = "free_beta";
    public const string ProbationPlan = "probationismo";
    public const string AnnualPlan = "annual";
    public static IReadOnlyList<string> Plans { get; } = Array.AsReadOnly(new[] { Plan, ProbationPlan, AnnualPlan });
    public static DateTimeOffset? ExpiresFor(string plan, DateTimeOffset startsUtc) => plan switch
    {
        Plan => null,
        ProbationPlan => startsUtc.AddMonths(3),
        AnnualPlan => startsUtc.AddYears(1),
        _ => throw new ArgumentException("Unknown subscription plan.", nameof(plan))
    };
    public static bool ValidAssignment(AssignSubscriptionRequest request)
    {
        if (!Plans.Contains(request.Plan) || (request.Version is not null && !ValidVersion(request.Version)) || !ValidReason(request.Reason)) return false;
        if (request.Plan == Plan) return request.StartsUtc is null;
        if (request.StartsUtc is null || request.StartsUtc.Value.Offset != TimeSpan.Zero) return false;
        try { _ = ExpiresFor(request.Plan, request.StartsUtc.Value); return true; }
        catch (ArgumentOutOfRangeException) { return false; }
    }
    public static string EffectiveState(string status, DateTimeOffset startsUtc, DateTimeOffset? expiresUtc, DateTimeOffset now)
    {
        if (status != "active") return status;
        if (now < startsUtc) return "scheduled";
        return expiresUtc is not null && now >= expiresUtc ? "expired" : "active";
    }
    public static IReadOnlyList<string> Statuses { get; } = Array.AsReadOnly(new[] { "active", "cancelled", "suspended" });
    public static IReadOnlyList<string> AuditActions { get; } = Array.AsReadOnly(new[]
    {
        "subscription.activated", "subscription.reactivated", "subscription.cancelled", "subscription.updated", "subscription.recovery_suspended", "subscription.assigned", "subscription.renewed"
    });
    public static bool ValidPage(int page, int pageSize) => page is >= 1 and <= 1000000 && pageSize is >= 1 and <= 100;
    public static bool ValidVersion(string? version) => !string.IsNullOrWhiteSpace(version) && version.Length <= 64 && !version.Any(char.IsControl);
    public static bool ValidReason(string? reason) => reason?.Trim().Length is >= 2 and <= 200 && !reason.Any(char.IsControl);
    public static bool ValidAdmin(AdminSubscriptionRequest request) => ValidVersion(request.Version) && Statuses.Contains(request.Status) && ValidReason(request.Reason);
    public static bool ValidAudit(DateTimeOffset? fromUtc, DateTimeOffset? toUtc, string? action, int page, int pageSize) =>
        ValidPage(page, pageSize) && (fromUtc is null || toUtc is null || fromUtc <= toUtc) && (action is null || AuditActions.Contains(action));
    public static bool Eligible(bool confirmedActive, string? status) => confirmedActive && (status is null or "cancelled");
}
