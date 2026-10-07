using Acropolis.Subscriptions.Application;

namespace Acropolis.Subscriptions.UnitTests;

public sealed class SubscriptionRulesTests
{
    [Theory]
    [InlineData(true, null, true)]
    [InlineData(true, "cancelled", true)]
    [InlineData(true, "active", false)]
    [InlineData(true, "suspended", false)]
    [InlineData(false, null, false)]
    [InlineData(false, "cancelled", false)]
    public void ActivationRequiresConfirmedActiveAccountAndDoesNotOverrideSuspension(bool active, string? state, bool expected) => Assert.Equal(expected, SubscriptionRules.Eligible(active, state));
    [Theory]
    [InlineData(1, 20, true)]
    [InlineData(1000000, 100, true)]
    [InlineData(0, 20, false)]
    [InlineData(1000001, 20, false)]
    [InlineData(1, 0, false)]
    [InlineData(1, 101, false)]
    public void PaginationIsBounded(int page, int size, bool expected) => Assert.Equal(expected, SubscriptionRules.ValidPage(page, size));
    [Theory]
    [InlineData("active", "Revisión autorizada", true)]
    [InlineData("cancelled", "Decisión explícita", true)]
    [InlineData("suspended", "Control de acceso", true)]
    [InlineData("pending", "Invalid status", false)]
    [InlineData("active", "x", false)]
    [InlineData("active", "bad\nreason", false)]
    public void AdministrativeMutationAcceptsOnlyFiniteStatesAndSafeReason(string state, string reason, bool expected) => Assert.Equal(expected, SubscriptionRules.ValidAdmin(new("version", state, reason)));
    [Fact]
    public void AuditDatesAndActionsAreValidated()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.True(SubscriptionRules.ValidAudit(now.AddDays(-1), now, "subscription.activated", 1, 20));
        Assert.True(SubscriptionRules.ValidAudit(null, null, null, 1, 20));
        Assert.False(SubscriptionRules.ValidAudit(now, now.AddDays(-1), null, 1, 20));
        Assert.False(SubscriptionRules.ValidAudit(null, null, "raw-sql", 1, 20));
        Assert.False(SubscriptionRules.ValidAdmin(new("", "active", "Reason")));
        Assert.False(SubscriptionRules.ValidVersion(new string('v', 65)));
        Assert.False(SubscriptionRules.ValidVersion("bad\0version"));
        Assert.False(SubscriptionRules.ValidReason(new string('x', 201)));
        Assert.False(SubscriptionRules.ValidReason(null));
    }
    [Fact]
    public void FailedResultPreservesCodeAndStatus()
    {
        Assert.True(SubscriptionResult<bool>.Ok(true).Succeeded);
        var result = SubscriptionResult<bool>.Fail("subscription_suspended", 403);
        Assert.False(result.Succeeded); Assert.Equal(403, result.Status); Assert.Equal("subscription_suspended", result.Error);
    }
}
