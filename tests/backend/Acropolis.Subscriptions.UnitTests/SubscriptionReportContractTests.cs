using System.Text.Json;
using Acropolis.Subscriptions.Application;

namespace Acropolis.Subscriptions.UnitTests;

public sealed class SubscriptionReportContractTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void CurrentSnapshotCannotBeMistakenForRecordedEventsInThePublicContract()
    {
        var generated = new DateTimeOffset(2026, 10, 7, 6, 0, 0, TimeSpan.Zero);
        var report = new SubscriptionReportView(generated, 3,
        [
            new("active", 2), new("cancelled", 1), new("suspended", 0)
        ]);

        var json = JsonSerializer.SerializeToElement(report, JsonOptions);

        Assert.Equal("current", json.GetProperty("scope").GetString());
        Assert.Equal(generated, json.GetProperty("generatedUtc").GetDateTimeOffset());
        Assert.Equal(3, json.GetProperty("total").GetInt64());
        var statuses = json.GetProperty("byStatus").EnumerateArray().ToArray();
        Assert.Equal(new[] { "active", "cancelled", "suspended" }, statuses.Select(value => value.GetProperty("key").GetString()));
        Assert.Equal(new long[] { 2, 1, 0 }, statuses.Select(value => value.GetProperty("count").GetInt64()));
        Assert.False(json.TryGetProperty("totalEvents", out _));
        Assert.False(json.TryGetProperty("byEvent", out _));
        Assert.False(json.TryGetProperty("fromUtc", out _));
        Assert.False(json.TryGetProperty("toUtc", out _));
        Assert.False(json.TryGetProperty("days", out _));
    }

    [Fact]
    public void EventContractPreservesUtcBoundsCalendarDaysAndZeroActivityWithoutInventingAccountsOrRevenue()
    {
        var from = new DateTimeOffset(2024, 2, 29, 0, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2024, 3, 2, 0, 0, 0, TimeSpan.Zero);
        var report = new SubscriptionEventReportView(to, from, to, 3,
            [new("activated", 2), new("unclassified", 1)],
        [
            new(new DateOnly(2024, 2, 29), 3, [new("activated", 2), new("unclassified", 1)]),
            new(new DateOnly(2024, 3, 1), 0, [new("activated", 0), new("unclassified", 0)])
        ]);

        var json = JsonSerializer.SerializeToElement(report, JsonOptions);

        Assert.Equal("recorded_events", json.GetProperty("scope").GetString());
        Assert.Equal(from, json.GetProperty("fromUtc").GetDateTimeOffset());
        Assert.Equal(to, json.GetProperty("toUtc").GetDateTimeOffset());
        Assert.Equal(TimeSpan.Zero, json.GetProperty("fromUtc").GetDateTimeOffset().Offset);
        Assert.Equal(TimeSpan.Zero, json.GetProperty("toUtc").GetDateTimeOffset().Offset);
        Assert.Equal(3, json.GetProperty("totalEvents").GetInt64());
        var days = json.GetProperty("days").EnumerateArray().ToArray();
        Assert.Equal(new[] { "2024-02-29", "2024-03-01" }, days.Select(value => value.GetProperty("dayUtc").GetString()));
        Assert.Equal(new long[] { 3, 0 }, days.Select(value => value.GetProperty("totalEvents").GetInt64()));
        Assert.Equal(new long[] { 0, 0 }, days[1].GetProperty("byEvent").EnumerateArray().Select(value => value.GetProperty("count").GetInt64()));
        Assert.Contains(json.GetProperty("byEvent").EnumerateArray(), value => value.GetProperty("key").GetString() == "unclassified" && value.GetProperty("count").GetInt64() == 1);
        Assert.False(json.TryGetProperty("byStatus", out _));
        Assert.False(json.TryGetProperty("total", out _));
        Assert.False(json.TryGetProperty("uniqueAccounts", out _));
        Assert.False(json.TryGetProperty("revenue", out _));
        Assert.False(json.TryGetProperty("renewals", out _));
    }
}
