using System.Globalization;

namespace Acropolis.Subscriptions.Application;

public sealed record SubscriptionEventInterval(DateOnly From, DateOnly To);
public sealed record SubscriptionEventCount(string Key, long Count);
public sealed record SubscriptionEventDay(DateOnly DayUtc, long TotalEvents, SubscriptionEventCount[] ByEvent);
public sealed record SubscriptionEventReportView(
    DateTimeOffset GeneratedUtc, DateTimeOffset FromUtc, DateTimeOffset ToUtc,
    long TotalEvents, SubscriptionEventCount[] ByEvent, SubscriptionEventDay[] Days)
{
    public string Scope => "recorded_events";
}

public interface ISubscriptionEventReportService
{
    Task<SubscriptionEventReportView> GetEventsAsync(SubscriptionEventInterval interval, CancellationToken token);
}

public static class SubscriptionEventReportRules
{
    public static IReadOnlyList<string> Keys { get; } = Array.AsReadOnly(new[]
    {
        "activated", "reactivated", "cancelled",
        "updated_active_cancelled", "updated_active_suspended",
        "updated_cancelled_active", "updated_cancelled_suspended",
        "updated_suspended_active", "updated_suspended_cancelled",
        "recovery_suspended", "assigned", "renewed", "unclassified"
    });

    public static bool ValidInterval(SubscriptionEventInterval? interval) =>
        interval is not null && interval.To.DayNumber - interval.From.DayNumber is >= 1 and <= 366;

    public static bool TryParseInterval(string? from, string? to, out SubscriptionEventInterval? interval)
    {
        interval = null;
        if (!TryParseDate(from, out var first) || !TryParseDate(to, out var last)) return false;
        var candidate = new SubscriptionEventInterval(first, last);
        if (!ValidInterval(candidate)) return false;
        interval = candidate;
        return true;
    }

    private static bool TryParseDate(string? value, out DateOnly date)
    {
        date = default;
        if (value is null || value.Length != 10 || value[4] != '-' || value[7] != '-') return false;
        for (var index = 0; index < value.Length; index++)
        {
            if (index is 4 or 7) continue;
            if (value[index] is < '0' or > '9') return false;
        }
        return DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }
}
