using System.Globalization;
using System.Text.Json;
using Acropolis.Subscriptions.Application;

namespace Acropolis.Subscriptions.UnitTests;

public sealed class SubscriptionPlanRulesTests
{
    [Theory]
    [InlineData("probationismo", "2026-10-07T12:00:00Z", "2027-01-07T12:00:00Z")]
    [InlineData("probationismo", "2023-01-31T17:25:00Z", "2023-04-30T17:25:00Z")]
    [InlineData("annual", "2024-02-29T10:45:00Z", "2025-02-28T10:45:00Z")]
    [InlineData("annual", "2026-10-07T12:00:00Z", "2027-10-07T12:00:00Z")]
    public void TermsUseNaturalCalendarPeriodsAndPreserveTheUtcTime(string plan, string start, string end) =>
        Assert.Equal(Utc(end), SubscriptionRules.ExpiresFor(plan, Utc(start)));

    [Fact]
    public void FreeHasNoArtificialDateOrTermAndFullPlansRequireAnExplicitSafeUtcStart()
    {
        Assert.Null(SubscriptionRules.ExpiresFor("free_beta", Utc("2026-10-07T12:00:00Z")));
        Assert.True(SubscriptionRules.ValidAssignment(new("free_beta", null, null, "Alta manual autorizada")));
        Assert.False(SubscriptionRules.ValidAssignment(new("free_beta", Utc("2026-10-07T12:00:00Z"), null, "Fecha artificial")));
        Assert.False(SubscriptionRules.ValidAssignment(new("annual", null, null, "Inicio ausente")));
        Assert.False(SubscriptionRules.ValidAssignment(new("annual", new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.FromHours(-5)), null, "Debe ser UTC")));
        Assert.False(SubscriptionRules.ValidAssignment(new("annual", DateTimeOffset.MaxValue, null, "Desborde de calendario")));
        Assert.False(SubscriptionRules.ValidAssignment(new("probationismo", DateTimeOffset.MaxValue, null, "Desborde de calendario")));
        Assert.False(SubscriptionRules.ValidAssignment(new("invented", null, null, "Plan no aprobado")));
        Assert.False(SubscriptionRules.ValidAssignment(new("free_beta", null, "", "Versión inválida")));
        Assert.False(SubscriptionRules.ValidAssignment(new("free_beta", null, null, "x")));
        Assert.False(SubscriptionRules.ValidAssignment(new("free_beta", null, null, "Motivo\ncon control")));
        Assert.Throws<ArgumentException>(() => SubscriptionRules.ExpiresFor("invented", Utc("2026-10-07T12:00:00Z")));
    }

    [Theory]
    [InlineData("active", -1, "scheduled")]
    [InlineData("active", 0, "active")]
    [InlineData("active", 59, "active")]
    [InlineData("active", 60, "expired")]
    [InlineData("cancelled", 0, "cancelled")]
    [InlineData("suspended", 0, "suspended")]
    public void EffectiveAccessHasAnInclusiveStartExclusiveEndAndNeverOverridesAdministrativeState(string status, int elapsed, string expected)
    {
        var starts = Utc("2026-10-07T12:00:00Z");
        Assert.Equal(expected, SubscriptionRules.EffectiveState(status, starts, starts.AddSeconds(60), starts.AddSeconds(elapsed)));
        Assert.Equal("active", SubscriptionRules.EffectiveState("active", starts, null, starts.AddYears(10)));
    }

    [Fact]
    public void PublicSubscriptionViewSeparatesStoredStatusFromEffectiveAvailabilityAndRealDates()
    {
        var starts = Utc("2026-10-07T12:00:00Z");
        var value = new SubscriptionView(Guid.NewGuid(), Guid.NewGuid(), "annual", "active", starts, starts, starts,
            null, starts.AddYears(1), "version", starts, "expired");
        var json = JsonSerializer.SerializeToElement(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal("active", json.GetProperty("status").GetString());
        Assert.Equal("expired", json.GetProperty("effectiveState").GetString());
        Assert.Equal(starts, json.GetProperty("startsUtc").GetDateTimeOffset());
        Assert.Equal(starts.AddYears(1), json.GetProperty("expiresUtc").GetDateTimeOffset());
        foreach (var field in new[] { "price", "paid", "invoice", "autoRenew" }) Assert.False(json.TryGetProperty(field, out _));
    }
    private static DateTimeOffset Utc(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);
}
