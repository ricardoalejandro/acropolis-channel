using Acropolis.Subscriptions.Application;

namespace Acropolis.Subscriptions.UnitTests;

public sealed class SubscriptionNotificationRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 22, 30, 0, TimeSpan.Zero);
    [Theory]
    [InlineData(604800, true)]
    [InlineData(604801, false)]
    [InlineData(1, true)]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    public void ReminderWindowIncludesSevenDaysAndExcludesExpiry(int secondsUntilEnd, bool expected)
    {
        Assert.Equal(expected, SubscriptionNotificationRules.ReminderDue("active", "annual", Now.AddMonths(-3), Now.AddSeconds(secondsUntilEnd), Now));
    }
    [Theory]
    [InlineData("suspended", "annual")]
    [InlineData("cancelled", "annual")]
    [InlineData("active", "free_beta")]
    [InlineData("active", "unknown")]
    public void OnlyActiveFullPlansHaveExpiryNotices(string status, string plan)
    {
        Assert.False(SubscriptionNotificationRules.ReminderDue(status, plan, Now.AddMonths(-3), Now.AddDays(7), Now));
    }
    [Fact]
    public void FutureAndNonExpiringPeriodsNeverSchedule()
    {
        Assert.False(SubscriptionNotificationRules.ReminderDue("active", "probationismo", Now.AddHours(1), Now.AddDays(7), Now));
        Assert.False(SubscriptionNotificationRules.ReminderDue("active", "annual", Now.AddMonths(-3), null, Now));
    }
    [Fact]
    public void ReminderKeysRepresentATermGenerationAndEventsRemainDistinct()
    {
        var subscription = Guid.NewGuid(); var generation = Guid.NewGuid(); var audit = Guid.NewGuid();
        var key = SubscriptionNotificationRules.ReminderKey(subscription, generation);
        Assert.Equal("expiring:" + subscription.ToString("N") + ":" + generation.ToString("N"), key);
        Assert.Equal(key, SubscriptionNotificationRules.ReminderKey(subscription, generation));
        Assert.NotEqual(key, SubscriptionNotificationRules.ReminderKey(subscription, Guid.NewGuid()));
        Assert.NotEqual(key, SubscriptionNotificationRules.ReminderKey(Guid.NewGuid(), generation));
        Assert.Equal("audit:" + audit.ToString("N"), SubscriptionNotificationRules.EventKey(audit));
        Assert.NotEqual(SubscriptionNotificationRules.EventKey(audit), SubscriptionNotificationRules.EventKey(Guid.NewGuid()));
        Assert.True(key.Length <= 96);
    }
    [Theory]
    [InlineData(0, 30)]
    [InlineData(1, 30)]
    [InlineData(2, 60)]
    [InlineData(3, 120)]
    [InlineData(4, 240)]
    [InlineData(5, 480)]
    [InlineData(100, 480)]
    public void RetriesAreBoundedAndNeverSpin(int attempts, int seconds) => Assert.Equal(TimeSpan.FromSeconds(seconds), SubscriptionNotificationRules.RetryDelay(attempts));
    [Theory]
    [InlineData("assigned", "Probacionismo", "probationismo", "asignado", "07/01/2027 17:30")]
    [InlineData("renewed", "Anual", "annual", "renovado", "07/10/2027 17:30")]
    [InlineData("expiring", "Anual", "annual", "próximo a vencer", "07/10/2027 17:30")]
    public void NoticesDescribePersistedTermsWithLimaTime(string kind, string label, string plan, string subjectFragment, string expectedEnd)
    {
        var notice = SubscriptionNotificationMessages.Create(kind, plan, Now, plan == "annual" ? Now.AddYears(1) : Now.AddMonths(3), "https://qa.example.test");
        Assert.Contains(subjectFragment, notice.Subject);
        Assert.Contains("Plan: " + label, notice.Text);
        Assert.Contains("Inicio: 07/10/2026 17:30 (hora de Lima, UTC-05:00)", notice.Text);
        Assert.Contains("Vencimiento: " + expectedEnd, notice.Text);
        Assert.Contains("La renovación es manual", notice.Text);
        Assert.Contains("https://qa.example.test/profile/subscription", notice.Text);
        Assert.DoesNotContain("exactamente 7 días", notice.Text);
        Assert.DoesNotContain("contraseña", notice.Text);
    }
    [Fact]
    public void FreeAssignmentHasNoExpiryOrPromiseOfFullAccess()
    {
        var notice = SubscriptionNotificationMessages.Create("assigned", "free_beta", Now, null, "https://qa.example.test/");
        Assert.Contains("Plan: Gratuito", notice.Text);
        Assert.Contains("Sin fecha de vencimiento", notice.Text);
        Assert.Contains("únicamente a las obras marcadas gratuitas", notice.Text);
        Assert.DoesNotContain("Vencimiento:", notice.Text);
        Assert.DoesNotContain("todo el catálogo", notice.Text);
        Assert.DoesNotContain("//profile", notice.Text);
    }
    [Theory]
    [InlineData("http://qa.example.test")]
    [InlineData("https://user@qa.example.test")]
    [InlineData("https://qa.example.test/path")]
    [InlineData("https://qa.example.test?token=invalid")]
    [InlineData("https://qa.example.test#fragment")]
    [InlineData("invalid")]
    public void LinksCannotUseAnUntrustedOrigin(string origin) => Assert.Throws<ArgumentException>(() => SubscriptionNotificationMessages.Create("assigned", "free_beta", Now, null, origin));
    [Fact]
    public void InvalidKindsPlansAndTermsCannotBePresentedAsANotice()
    {
        const string origin = "https://qa.example.test";
        Assert.Throws<ArgumentException>(() => SubscriptionNotificationMessages.Create("marketing", "free_beta", Now, null, origin));
        Assert.Throws<ArgumentException>(() => SubscriptionNotificationMessages.Create("assigned", "unknown", Now, null, origin));
        Assert.Throws<ArgumentException>(() => SubscriptionNotificationMessages.Create("assigned", "free_beta", Now, Now.AddDays(7), origin));
        Assert.Throws<ArgumentException>(() => SubscriptionNotificationMessages.Create("expiring", "free_beta", Now, null, origin));
        Assert.Throws<ArgumentException>(() => SubscriptionNotificationMessages.Create("assigned", "annual", Now, null, origin));
        Assert.Throws<ArgumentException>(() => SubscriptionNotificationMessages.Create("assigned", "annual", Now, Now, origin));
    }
}
