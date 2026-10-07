using Acropolis.Subscriptions.Application;

namespace Acropolis.Subscriptions.UnitTests;

public sealed class SubscriptionEventReportRulesTests
{
    [Theory]
    [InlineData("2026-10-01", "2026-10-02", 1)]
    [InlineData("2024-02-29", "2024-03-01", 1)]
    [InlineData("2024-01-01", "2025-01-01", 366)]
    [InlineData("0001-01-01", "0001-01-02", 1)]
    [InlineData("9999-12-30", "9999-12-31", 1)]
    public void ExactCalendarDatesPreserveTheirHalfOpenInterval(string from, string to, int days)
    {
        Assert.True(SubscriptionEventReportRules.TryParseInterval(from, to, out var interval));
        Assert.NotNull(interval);
        Assert.Equal(from, interval.From.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(to, interval.To.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(days, interval.To.DayNumber - interval.From.DayNumber);
        Assert.True(SubscriptionEventReportRules.ValidInterval(interval));
    }

    [Theory]
    [InlineData(null, "2026-10-02")]
    [InlineData("2026-10-01", null)]
    [InlineData("", "2026-10-02")]
    [InlineData("2026-10-01", "")]
    [InlineData(" 2026-10-01", "2026-10-02")]
    [InlineData("2026-10-01", "2026-10-02 ")]
    [InlineData("2026-1-01", "2026-10-02")]
    [InlineData("2026-10-1", "2026-10-02")]
    [InlineData("2026/10/01", "2026-10-02")]
    [InlineData("2026-10-01T00:00:00Z", "2026-10-02")]
    [InlineData("2026-10-01", "2026-10-02T00:00:00+00:00")]
    [InlineData("2026-10-01+05:00", "2026-10-02")]
    [InlineData("２０２６-１０-０１", "2026-10-02")]
    [InlineData("2026-02-29", "2026-03-01")]
    [InlineData("2026-04-31", "2026-05-01")]
    [InlineData("0000-01-01", "0001-01-02")]
    [InlineData("2026-10-02", "2026-10-02")]
    [InlineData("2026-10-03", "2026-10-02")]
    [InlineData("2024-01-01", "2025-01-02")]
    [InlineData("2026-10-01\0", "2026-10-02")]
    public void MissingNonAsciiCoercedOrOutOfRangeDatesCannotBecomeAnInterval(string? from, string? to)
    {
        SubscriptionEventInterval? interval = new(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 2));
        Assert.False(SubscriptionEventReportRules.TryParseInterval(from, to, out interval));
        Assert.Null(interval);
    }

    [Fact]
    public void DirectIntervalsRequireOneTo366CalendarDays()
    {
        var first = new DateOnly(2024, 1, 1);
        Assert.False(SubscriptionEventReportRules.ValidInterval(null));
        Assert.False(SubscriptionEventReportRules.ValidInterval(new(first, first)));
        Assert.False(SubscriptionEventReportRules.ValidInterval(new(first, first.AddDays(-1))));
        Assert.False(SubscriptionEventReportRules.ValidInterval(new(first, first.AddDays(367))));
        Assert.True(SubscriptionEventReportRules.ValidInterval(new(first, first.AddDays(1))));
        Assert.True(SubscriptionEventReportRules.ValidInterval(new(first, first.AddDays(366))));
    }

    [Fact]
    public void PublicBucketKeysAreCompleteFiniteAndDoNotPretendToBeRenewals()
    {
        Assert.Equal(new[]
        {
            "activated", "reactivated", "cancelled", "updated_active_cancelled", "updated_active_suspended",
            "updated_cancelled_active", "updated_cancelled_suspended", "updated_suspended_active",
            "updated_suspended_cancelled", "recovery_suspended", "unclassified"
        }, SubscriptionEventReportRules.Keys);
        Assert.Equal(11, SubscriptionEventReportRules.Keys.Distinct(StringComparer.Ordinal).Count());
        Assert.DoesNotContain("renewed", SubscriptionEventReportRules.Keys);
    }
}
