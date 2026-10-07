using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Acropolis.Identity.Application;
using Acropolis.Identity.IntegrationTests;
using Acropolis.Subscriptions.Infrastructure;
using Acropolis.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace Acropolis.Subscriptions.IntegrationTests;

[Collection("IdentityPostgres")]
public sealed class SubscriptionNotificationHttpTests(IdentityFixture database)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task RealAdministrativeAssignmentWithMfaQueuesOnceWithoutExposingDeliveryDetailsOrSendingAutomatically()
    {
        await database.ResetAsync(Token); await using var api = new SubscriptionNotificationTestHost(database);
        var manager = await api.AccountAsync("http-notice-manager", manager: true);
        var member = await api.AccountAsync("http-notice-holder");
        using var admin = api.Client(); using var holder = api.Client();
        using var login = await MfaTestClient.Login(admin, manager.Email!, SubscriptionNotificationTestHost.Password, Token);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var route = $"/api/v1/admin/subscriptions/accounts/{member.Id}/assign";
        var request = new { plan = "annual", startsUtc = api.Clock.GetUtcNow(), version = (string?)null, reason = "Synthetic authorized assignment" };
        using var accepted = await SubscriptionNotificationTestHost.WriteAsync(admin, route, request);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode); Assert.True(accepted.Headers.CacheControl?.NoStore);
        var body = await accepted.Content.ReadFromJsonAsync<JsonElement>(Token);
        Assert.Equal(member.Id, body.GetProperty("userId").GetGuid()); Assert.Equal("annual", body.GetProperty("plan").GetString());
        foreach (var forbidden in new[] { "notifications", "recipient", "messageId", "deliveryStatus", "smtp", "email" }) Assert.False(body.TryGetProperty(forbidden, out _));
        using var duplicate = await SubscriptionNotificationTestHost.WriteAsync(admin, route, request);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal("concurrency_conflict", (await duplicate.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("code").GetString());
        Assert.Empty(api.Sender.Messages); Assert.Empty(api.Sender.AttemptedIds);
        using var holderLogin = await MfaTestClient.Login(holder, member.Email!, SubscriptionNotificationTestHost.Password, Token);
        Assert.Equal(HttpStatusCode.OK, holderLogin.StatusCode);
        using var mine = await holder.GetAsync("/api/v1/subscriptions/me", Token);
        Assert.Equal(HttpStatusCode.OK, mine.StatusCode); Assert.True(mine.Headers.CacheControl?.NoStore);
        var mineBody = await mine.Content.ReadAsStringAsync(Token);
        Assert.DoesNotContain("NotificationOutbox", mineBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("deliveryStatus", mineBody, StringComparison.OrdinalIgnoreCase);
        await using (var context = Context())
        {
            var intent = await context.Notifications.AsNoTracking().SingleAsync(Token);
            Assert.Equal("pending", intent.Status); Assert.Equal(0, intent.Attempts);
            Assert.Single(await context.Audit.AsNoTracking().ToArrayAsync(Token));
        }
        Assert.True(await api.DispatchAsync()); Assert.Equal(member.Email, Assert.Single(api.Sender.Messages).Recipient);
    }

    [Fact]
    public async Task AnonymousWrongPermissionAndPasswordOnlyChallengeCannotCreateAnIntent()
    {
        await database.ResetAsync(Token); await using var api = new SubscriptionNotificationTestHost(database);
        var manager = await api.AccountAsync("http-denied-notice-manager", manager: true);
        var member = await api.AccountAsync("http-denied-notice-holder");
        using var anonymous = api.Client(); using var wrongPermission = api.Client(); using var passwordOnly = api.Client();
        var route = $"/api/v1/admin/subscriptions/accounts/{member.Id}/assign";
        var request = new { plan = "annual", startsUtc = api.Clock.GetUtcNow(), version = (string?)null, reason = "Synthetic forbidden assignment" };
        using var noSession = await SubscriptionNotificationTestHost.WriteAsync(anonymous, route, request);
        Assert.Equal(HttpStatusCode.Unauthorized, noSession.StatusCode);
        using var ordinaryLogin = await MfaTestClient.Login(wrongPermission, member.Email!, SubscriptionNotificationTestHost.Password, Token);
        Assert.Equal(HttpStatusCode.OK, ordinaryLogin.StatusCode);
        using var denied = await SubscriptionNotificationTestHost.WriteAsync(wrongPermission, route, request);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using var challenge = await MfaTestClient.Write(passwordOnly, "login", new LoginRequest(manager.Email!, SubscriptionNotificationTestHost.Password), Token);
        Assert.Equal(HttpStatusCode.Accepted, challenge.StatusCode); // No full authenticated session before a verified second factor.
        using var noMfaSession = await SubscriptionNotificationTestHost.WriteAsync(passwordOnly, route, request);
        Assert.Equal(HttpStatusCode.Unauthorized, noMfaSession.StatusCode);
        await using var context = Context();
        Assert.Empty(await context.Notifications.AsNoTracking().ToArrayAsync(Token));
        Assert.Empty(await context.Subscriptions.AsNoTracking().ToArrayAsync(Token)); Assert.Empty(await context.Audit.AsNoTracking().ToArrayAsync(Token));
        Assert.False(await api.DispatchAsync()); Assert.Empty(api.Sender.Messages);
    }

    [Fact]
    public async Task MissingCsrfAndUnknownFieldsCannotPartiallyPersistSubscriptionOrMailIntent()
    {
        await database.ResetAsync(Token); await using var api = new SubscriptionNotificationTestHost(database);
        var manager = await api.AccountAsync("http-invalid-notice-manager", manager: true);
        var member = await api.AccountAsync("http-invalid-notice-holder");
        using var admin = api.Client();
        using var login = await MfaTestClient.Login(admin, manager.Email!, SubscriptionNotificationTestHost.Password, Token);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var route = $"/api/v1/admin/subscriptions/accounts/{member.Id}/assign";
        var request = new { plan = "annual", startsUtc = api.Clock.GetUtcNow(), version = (string?)null, reason = "Synthetic malformed assignment" };
        using var missing = await admin.PostAsJsonAsync(route, request, Token);
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        using var unknown = await SubscriptionNotificationTestHost.WriteAsync(admin, route,
            new { request.plan, request.startsUtc, request.version, request.reason, sendMarketing = true });
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        await using var context = Context();
        Assert.Empty(await context.Subscriptions.AsNoTracking().ToArrayAsync(Token)); Assert.Empty(await context.Audit.AsNoTracking().ToArrayAsync(Token));
        Assert.Empty(await context.Notifications.AsNoTracking().ToArrayAsync(Token)); Assert.Empty(api.Sender.Messages);
    }

    private SubscriptionsDbContext Context()
    {
        var options = new DbContextOptionsBuilder<SubscriptionsDbContext>();
        SubscriptionsRegistration.ConfigureDatabase(options, database.RuntimeConnection);
        return new(options.Options);
    }
}
