using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Acropolis.Identity.Application;
using Acropolis.Identity.Infrastructure;
using Acropolis.Identity.IntegrationTests;
using Acropolis.Migrations;
using Acropolis.Subscriptions.Application;
using Acropolis.Subscriptions.Infrastructure;
using Acropolis.TestSupport;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Acropolis.Subscriptions.IntegrationTests;

[Collection("IdentityPostgres")]
public sealed class SubscriptionsTests(IdentityFixture database)
{
    private const string Password = "Acropolis subscription beta phrase";
    [Fact]
    public async Task ExplicitFreeActivationCancellationAndSuspensionUseRealHttpCsrfAndIndependentAuthority()
    {
        var token = TestContext.Current.CancellationToken;
        await database.ResetAsync(token);
        await using var api = new IdentityApiFactory(database);
        var user = await Create(api, "subscription-member@example.test", token);
        var editor = await Create(api, "subscription-manager@example.test", token, subscriptions: true);
        var userManager = await Create(api, "users-manager@example.test", token, users: true);
        using var client = api.Client(); using var admin = api.Client(); using var users = api.Client(); using var anonymous = api.Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/subscriptions/me", token)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await MfaTestClient.Login(client, user.Email!, Password, token)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await MfaTestClient.Login(admin, editor.Email!, Password, token)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await MfaTestClient.Login(users, userManager.Email!, Password, token)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await users.GetAsync("/api/v1/admin/subscriptions", token)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/admin/subscriptions/accounts", token)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await users.GetAsync("/api/v1/admin/subscriptions/accounts", token)).StatusCode);
        var directory = (await admin.GetFromJsonAsync<JsonElement>("/api/v1/admin/subscriptions/accounts?search=subscription-member&pageSize=1", token));
        Assert.Equal(1, directory.GetProperty("total").GetInt32());
        var account = directory.GetProperty("items")[0]; Assert.Equal(user.Id, account.GetProperty("id").GetGuid());
        Assert.Equal(new[] { "displayName", "email", "emailConfirmed", "id", "status" }, account.EnumerateObject().Select(x => x.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync("/api/v1/admin/subscriptions/accounts?page=0", token)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync("/api/v1/admin/subscriptions/accounts?search=" + new string('a', 201), token)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync("/api/v1/admin/subscriptions/accounts?search=%00", token)).StatusCode);
        var empty = (await client.GetFromJsonAsync<SubscriptionStatusView>("/api/v1/subscriptions/me", token))!;
        Assert.Null(empty.Subscription); Assert.True(empty.EligibleToActivate);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/subscriptions/activate", new { }, token)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Write(client, HttpMethod.Post, "/api/v1/subscriptions/activate", new { userId = editor.Id }, token)).StatusCode);
        using var activation = await Write(client, HttpMethod.Post, "/api/v1/subscriptions/activate", new { }, token);
        Assert.Equal(HttpStatusCode.OK, activation.StatusCode);
        var row = (await activation.Content.ReadFromJsonAsync<SubscriptionView>(token))!;
        Assert.Equal("free_beta", row.Plan); Assert.Equal("active", row.Status); Assert.Null(row.ExpiresUtc); Assert.Null(row.CancelledUtc);
        var again = (await (await Write(client, HttpMethod.Post, "/api/v1/subscriptions/activate", new { }, token)).Content.ReadFromJsonAsync<SubscriptionView>(token))!;
        Assert.Equal(row.Version, again.Version); Assert.Equal(row.Id, again.Id);
        Assert.False((await client.GetFromJsonAsync<SubscriptionStatusView>("/api/v1/subscriptions/me", token))!.EligibleToActivate);
        Assert.Equal(HttpStatusCode.Conflict, (await Write(client, HttpMethod.Post, "/api/v1/subscriptions/cancel", new CancelSubscriptionRequest("stale"), token)).StatusCode);
        var cancelled = (await (await Write(client, HttpMethod.Post, "/api/v1/subscriptions/cancel", new CancelSubscriptionRequest(row.Version), token)).Content.ReadFromJsonAsync<SubscriptionView>(token))!;
        Assert.Equal("cancelled", cancelled.Status); Assert.NotNull(cancelled.CancelledUtc); Assert.NotEqual(row.Version, cancelled.Version);
        var repeat = (await (await Write(client, HttpMethod.Post, "/api/v1/subscriptions/cancel", new CancelSubscriptionRequest(cancelled.Version), token)).Content.ReadFromJsonAsync<SubscriptionView>(token))!;
        Assert.Equal(cancelled.Version, repeat.Version);
        Assert.True((await client.GetFromJsonAsync<SubscriptionStatusView>("/api/v1/subscriptions/me", token))!.EligibleToActivate);
        row = (await (await Write(client, HttpMethod.Post, "/api/v1/subscriptions/activate", new { }, token)).Content.ReadFromJsonAsync<SubscriptionView>(token))!;
        var activePage = (await admin.GetFromJsonAsync<SubscriptionPage>($"/api/v1/admin/subscriptions?status=active&userId={user.Id}&pageSize=1", token))!;
        Assert.Single(activePage.Items); Assert.Equal(1, activePage.Total);
        Assert.Equal(row.Id, (await admin.GetFromJsonAsync<SubscriptionView>($"/api/v1/admin/subscriptions/{row.Id}", token))!.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/v1/admin/subscriptions/{Guid.NewGuid()}", token)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync("/api/v1/admin/subscriptions?status=pending", token)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync("/api/v1/admin/subscriptions?pageSize=101", token)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Write(admin, HttpMethod.Patch, $"/api/v1/admin/subscriptions/{row.Id}", new AdminSubscriptionRequest(row.Version, "suspended", "x"), token)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Write(admin, HttpMethod.Patch, $"/api/v1/admin/subscriptions/{row.Id}", new AdminSubscriptionRequest("stale", "suspended", "Revisión de acceso"), token)).StatusCode);
        var suspended = (await (await Write(admin, HttpMethod.Patch, $"/api/v1/admin/subscriptions/{row.Id}", new AdminSubscriptionRequest(row.Version, "suspended", "Revisión de acceso"), token)).Content.ReadFromJsonAsync<SubscriptionView>(token))!;
        Assert.Equal("suspended", suspended.Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Write(client, HttpMethod.Post, "/api/v1/subscriptions/activate", new { }, token)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Write(client, HttpMethod.Post, "/api/v1/subscriptions/cancel", new CancelSubscriptionRequest(suspended.Version), token)).StatusCode);
        Assert.False((await client.GetFromJsonAsync<SubscriptionStatusView>("/api/v1/subscriptions/me", token))!.EligibleToActivate);
        var same = (await (await Write(admin, HttpMethod.Patch, $"/api/v1/admin/subscriptions/{row.Id}", new AdminSubscriptionRequest(suspended.Version, "suspended", "Sin cambios reales"), token)).Content.ReadFromJsonAsync<SubscriptionView>(token))!;
        Assert.Equal(suspended.Version, same.Version);
        var resumed = (await (await Write(admin, HttpMethod.Patch, $"/api/v1/admin/subscriptions/{row.Id}", new AdminSubscriptionRequest(suspended.Version, "active", "Revisión completada"), token)).Content.ReadFromJsonAsync<SubscriptionView>(token))!;
        Assert.Equal("active", resumed.Status); Assert.Null(resumed.CancelledUtc);
        // The preceding events deliberately tie; the final change must be strictly newer for this page assertion.
        api.Clock.Advance(TimeSpan.FromSeconds(1));
        var administrativeCancellation = (await (await Write(admin, HttpMethod.Patch, $"/api/v1/admin/subscriptions/{row.Id}", new AdminSubscriptionRequest(resumed.Version, "cancelled", "Cancelación administrativa"), token)).Content.ReadFromJsonAsync<SubscriptionView>(token))!;
        Assert.Equal("cancelled", administrativeCancellation.Status); Assert.NotNull(administrativeCancellation.CancelledUtc);
        Assert.Equal(1, (await admin.GetFromJsonAsync<SubscriptionPage>("/api/v1/admin/subscriptions", token))!.Total);
        var audit = (await admin.GetFromJsonAsync<SubscriptionAuditPage>($"/api/v1/admin/subscriptions/audit?subscriptionId={row.Id}&userId={user.Id}&fromUtc=2000-01-01T00%3A00%3A00Z&toUtc=2099-01-01T00%3A00%3A00Z&pageSize=2", token))!;
        Assert.Equal(6, audit.Total); Assert.Equal(2, audit.Items.Length); Assert.Contains(audit.Items, x => x.Action == "subscription.updated");
        Assert.Equal("subscription.updated", audit.Items[0].Action);
        Assert.Equal("active", audit.Items[0].BeforeStatus); Assert.Equal("cancelled", audit.Items[0].AfterStatus);
        Assert.Equal(1, audit.Page); Assert.Equal(2, audit.PageSize);
        var events = audit.Items.ToList();
        for (var page = 2; page <= 3; page++)
        {
            var next = (await admin.GetFromJsonAsync<SubscriptionAuditPage>($"/api/v1/admin/subscriptions/audit?subscriptionId={row.Id}&userId={user.Id}&fromUtc=2000-01-01T00%3A00%3A00Z&toUtc=2099-01-01T00%3A00%3A00Z&pageSize=2&page={page}", token))!;
            Assert.Equal(6, next.Total); Assert.Equal(2, next.Items.Length);
            Assert.Equal(page, next.Page); Assert.Equal(2, next.PageSize);
            events.AddRange(next.Items);
        }
        Assert.Equal(6, events.Count); Assert.Equal(6, events.Select(x => x.Id).Distinct().Count());
        Assert.Contains(events, x => x.Action == "subscription.activated" && x.BeforeStatus is null && x.AfterStatus == "active");
        Assert.Contains(events, x => x.Action == "subscription.cancelled" && x.BeforeStatus == "active" && x.AfterStatus == "cancelled");
        Assert.Contains(events, x => x.Action == "subscription.reactivated" && x.BeforeStatus == "cancelled" && x.AfterStatus == "active");
        Assert.Contains(events, x => x.Action == "subscription.updated" && x.BeforeStatus == "active" && x.AfterStatus == "suspended");
        Assert.Contains(events, x => x.Action == "subscription.updated" && x.BeforeStatus == "suspended" && x.AfterStatus == "active");
        Assert.Contains(events, x => x.Action == "subscription.updated" && x.BeforeStatus == "active" && x.AfterStatus == "cancelled");
        var tiedUtc = events[1].CreatedUtc;
        Assert.All(events.Skip(1), item => Assert.Equal(tiedUtc, item.CreatedUtc));
        Assert.Equal(TimeSpan.FromSeconds(1), events[0].CreatedUtc - tiedUtc);
        // Canonical UUID hex follows PostgreSQL byte ordering; Guid.CompareTo does not express that contract.
        var orderedIds = events.OrderByDescending(x => x.CreatedUtc)
            .ThenByDescending(x => x.Id.ToString("N"), StringComparer.Ordinal).Select(x => x.Id);
        Assert.Equal(orderedIds, events.Select(x => x.Id));
        Assert.Single((await admin.GetFromJsonAsync<SubscriptionAuditPage>("/api/v1/admin/subscriptions/audit?action=subscription.activated", token))!.Items);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync("/api/v1/admin/subscriptions/audit?action=unknown", token)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync("/api/v1/admin/subscriptions/audit?fromUtc=2027-01-01T00%3A00%3A00Z&toUtc=2026-01-01T00%3A00%3A00Z", token)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync("/api/v1/admin/subscriptions/audit?page=0", token)).StatusCode);
    }
    [Fact]
    public async Task ConcurrentActivationHasOneRowAndConcurrentCancellationHasOneWinner()
    {
        var token = TestContext.Current.CancellationToken;
        await database.ResetAsync(token);
        await using var api = new IdentityApiFactory(database);
        var user = await Create(api, "subscription-concurrency@example.test", token);
        async Task<SubscriptionResult<SubscriptionView>> Activate()
        {
            using var scope = api.Services.CreateScope(); return await scope.ServiceProvider.GetRequiredService<ISubscriptionService>().ActivateAsync(user.Id, token);
        }
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Activate()));
        Assert.All(results, x => Assert.True(x.Succeeded));
        Assert.Single(results.Select(x => x.Value!.Id).Distinct()); Assert.Single(results.Select(x => x.Value!.Version).Distinct());
        var row = results[0].Value!;
        async Task<SubscriptionResult<SubscriptionView>> Cancel()
        {
            using var scope = api.Services.CreateScope(); return await scope.ServiceProvider.GetRequiredService<ISubscriptionService>().CancelAsync(user.Id, new(row.Version), token);
        }
        var cancelled = await Task.WhenAll(Cancel(), Cancel());
        Assert.Single(cancelled, x => x.Succeeded); Assert.Equal("concurrency_conflict", cancelled.Single(x => !x.Succeeded).Error);
        await using var context = Context();
        Assert.Equal(1, await context.Subscriptions.CountAsync(token)); Assert.Equal(2, await context.Audit.CountAsync(token));
        Assert.Equal("cancelled", (await context.Subscriptions.SingleAsync(token)).Status);
    }
    [Fact]
    public async Task OwnerSubscriptionIsProtectedAndRecoveryNeverRestoresActiveAccessOrGrant()
    {
        var token = TestContext.Current.CancellationToken;
        await database.ResetAsync(token);
        await using var api = new IdentityApiFactory(database);
        var owner = await Create(api, "subscription-owner@example.test", token);
        var other = await Create(api, "subscription-other@example.test", token, subscriptions: true);
        var cancelledUser = await Create(api, "subscription-cancelled@example.test", token);
        await using (var identity = database.Context(true)) await new IdentityOperations(identity, api.Clock).BootstrapOwnerAsync(owner.Email!, owner.Email, token);
        SubscriptionView owned;
        using (var scope = api.Services.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ISubscriptionService>();
            Assert.False(await scope.ServiceProvider.GetRequiredService<ISubscriptionAccess>().HasActiveAsync(owner.Id, token));
            owned = (await service.ActivateAsync(owner.Id, token)).Value!;
            Assert.True(await scope.ServiceProvider.GetRequiredService<ISubscriptionAccess>().HasActiveAsync(owner.Id, token));
        }
        using (var scope = api.Services.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ISubscriptionService>();
            Assert.Equal("owner_protected", (await service.UpdateAsync(other.Id, owned.Id, new(owned.Version, "suspended", "Unauthorized owner change"), token)).Error);
            Assert.Equal("not_found", (await service.UpdateAsync(other.Id, Guid.NewGuid(), new(owned.Version, "suspended", "Missing target"), token)).Error);
            var noOp = await service.UpdateAsync(owner.Id, owned.Id, new(owned.Version, "active", "No actual changes"), token);
            Assert.Equal(owned.Version, noOp.Value!.Version);
            var cancelled = (await service.ActivateAsync(cancelledUser.Id, token)).Value!;
            await service.CancelAsync(cancelledUser.Id, new(cancelled.Version), token);
        }
        await using (var context = Context(true))
        {
            context.Database.SetCommandTimeout(31);
            var operations = new SubscriptionOperations(context, api.Clock);
            await operations.InvalidateRecoveryAsync(token);
            Assert.Equal(31, context.Database.GetCommandTimeout());
            await operations.InvalidateRecoveryAsync(token);
            Assert.Equal(31, context.Database.GetCommandTimeout());
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operations.InvalidateRecoveryAsync(cancelled.Token));
            Assert.Equal(31, context.Database.GetCommandTimeout());
        }
        using (var scope = api.Services.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ISubscriptionService>();
            Assert.False(await scope.ServiceProvider.GetRequiredService<ISubscriptionAccess>().HasActiveAsync(owner.Id, token));
            Assert.Equal("subscription_suspended", (await service.ActivateAsync(owner.Id, token)).Error);
            var page = await service.ListAsync("cancelled", null, 1, 20, token); Assert.Single(page.Items); Assert.Equal(cancelledUser.Id, page.Items[0].UserId);
        }
        await using var final = Context();
        Assert.Equal(1, await final.Audit.CountAsync(x => x.Action == "subscription.recovery_suspended", token));
        await Assert.ThrowsAsync<PostgresException>(() => final.Database.ExecuteSqlRawAsync("DELETE FROM subscriptions.\"Subscriptions\"", token));
        await Assert.ThrowsAsync<PostgresException>(() => final.Database.ExecuteSqlRawAsync("UPDATE subscriptions.\"Audit\" SET \"Reason\"='changed'", token));
    }
    [Fact]
    public async Task LiveAccountEligibilityAndAdministrativeChecksCannotBeBypassedWithServiceCalls()
    {
        var token = TestContext.Current.CancellationToken;
        await database.ResetAsync(token);
        await using var api = new IdentityApiFactory(database);
        var member = await Create(api, "subscription-eligibility@example.test", token);
        var pending = await Create(api, "subscription-pending@example.test", token, confirmed: false);
        var administrator = await Create(api, "subscription-eligibility-admin@example.test", token, subscriptions: true);
        SubscriptionView row;
        using (var scope = api.Services.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ISubscriptionService>();
            Assert.Equal("account_not_active", (await service.ActivateAsync(pending.Id, token)).Error);
            Assert.Equal("account_not_active", (await service.ActivateAsync(Guid.NewGuid(), token)).Error);
            Assert.False((await service.GetMineAsync(pending.Id, token)).EligibleToActivate);
            Assert.Equal("not_found", (await service.CancelAsync(member.Id, new("version"), token)).Error);
            Assert.Equal("validation_error", (await service.CancelAsync(member.Id, new(""), token)).Error);
            row = (await service.ActivateAsync(member.Id, token)).Value!;
        }
        await using (var identity = database.Context(true)) await identity.Users.Where(x => x.Id == member.Id).ExecuteUpdateAsync(setters => setters.SetProperty(x => x.IsDisabled, true), token);
        using (var scope = api.Services.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ISubscriptionService>();
            Assert.Equal("account_not_active", (await service.CancelAsync(member.Id, new(row.Version), token)).Error);
            Assert.Equal("forbidden", (await service.UpdateAsync(pending.Id, row.Id, new(row.Version, "suspended", "Not an administrator"), token)).Error);
            Assert.Equal("account_not_active", (await service.UpdateAsync(administrator.Id, row.Id, new(row.Version, "active", "Disabled account"), token)).Error);
            var cancelled = (await service.UpdateAsync(administrator.Id, row.Id, new(row.Version, "cancelled", "Account disabled"), token)).Value!;
            Assert.Equal("cancelled", cancelled.Status); Assert.NotNull(cancelled.CancelledUtc);
        }
    }
    [Fact]
    public async Task SelfWriteRateBudgetBelongsToTheAccountAndKeepsReadAndOtherAccountsAvailable()
    {
        var token = TestContext.Current.CancellationToken;
        await database.ResetAsync(token);
        await using var api = new IdentityApiFactory(database);
        var first = await Create(api, "subscription-rate-first@example.test", token);
        var second = await Create(api, "subscription-rate-second@example.test", token);
        using var client = api.Client(); using var other = api.Client(); using var newSession = api.Client();
        Assert.Equal(HttpStatusCode.OK, (await MfaTestClient.Login(client, first.Email!, Password, token)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await MfaTestClient.Login(other, second.Email!, Password, token)).StatusCode);
        for (var index = 0; index < 20; index++)
        {
            using var accepted = await Write(client, HttpMethod.Post, "/api/v1/subscriptions/activate", new { }, token);
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        }
        using var denied = await Write(client, HttpMethod.Post, "/api/v1/subscriptions/activate", new { }, token);
        Assert.Equal(HttpStatusCode.TooManyRequests, denied.StatusCode); Assert.True(denied.Headers.CacheControl?.NoStore);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/subscriptions/me", token)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Write(other, HttpMethod.Post, "/api/v1/subscriptions/activate", new { }, token)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await MfaTestClient.Login(newSession, first.Email!, Password, token)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Write(newSession, HttpMethod.Post, "/api/v1/subscriptions/activate", new { }, token)).StatusCode);
        await using var context = Context(); Assert.Equal(2, await context.Subscriptions.CountAsync(token)); Assert.Equal(2, await context.Audit.CountAsync(token));
    }

    [Fact]
    public async Task RecoveryCliFailureBetweenModulesKeepsAccessClosedAndRetryIdempotent()
    {
        var token = TestContext.Current.CancellationToken;
        await database.ResetAsync(token);
        await using var api = new IdentityApiFactory(database);
        var member = await Create(api, "recovery-phase-member@example.test", token);
        var owner = await Create(api, "recovery-phase-owner@example.test", token);
        await using (var identity = database.Context(true))
            await new IdentityOperations(identity, api.Clock).BootstrapOwnerAsync(owner.Email!, owner.Email, token);
        await using (var identity = database.Context())
        {
            var designated = await identity.Users.AsNoTracking().SingleAsync(account => account.Id == owner.Id, token);
            Assert.True(designated.IsOwner);
            Assert.True(designated.UsersManage);
            Assert.True(designated.ContentManage);
            Assert.True(designated.SubscriptionsManage);
        }
        using var client = api.Client();
        using var login = await MfaTestClient.Login(client, member.Email!, Password, token);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        SubscriptionView active;
        using (var scope = api.Services.CreateScope())
        {
            var activation = await scope.ServiceProvider.GetRequiredService<ISubscriptionService>().ActivateAsync(member.Id, token);
            Assert.True(activation.Succeeded);
            active = activation.Value!;
        }
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/subscriptions/me", token)).StatusCode);

        // Invoke the actual async CLI Main without duplicating its two-phase orchestration
        // or adding a production test hook. This collection already runs without parallelism.
        var entryPoint = typeof(IdentityOperations).Assembly.GetType("Acropolis.Migrations.EntryPoint", throwOnError: true)!;
        var runCli = entryPoint.GetMethod("Main", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .CreateDelegate<Func<string[], Task<int>>>();
        var previousConnection = Environment.GetEnvironmentVariable("ConnectionStrings__Database");
        const string removeFailure = """
            DROP TRIGGER IF EXISTS qa_recovery_second_phase_failure ON subscriptions."Subscriptions";
            DROP FUNCTION IF EXISTS subscriptions.qa_recovery_second_phase_failure();
            """;
        try
        {
            await using (var failure = Context(true))
                await failure.Database.ExecuteSqlRawAsync("""
                    CREATE FUNCTION subscriptions.qa_recovery_second_phase_failure() RETURNS trigger
                    LANGUAGE plpgsql AS $qa$
                    BEGIN
                        RAISE EXCEPTION 'Synthetic QA failure in recovery subscription phase' USING ERRCODE='23514';
                    END;
                    $qa$;
                    CREATE TRIGGER qa_recovery_second_phase_failure BEFORE UPDATE OF "Status"
                    ON subscriptions."Subscriptions" FOR EACH ROW
                    WHEN (OLD."Status"='active' AND NEW."Status"='suspended')
                    EXECUTE FUNCTION subscriptions.qa_recovery_second_phase_failure();
                    """, token);
            Environment.SetEnvironmentVariable("ConnectionStrings__Database", database.MigrationConnection);
            Assert.Equal(1, await runCli(["recovery-invalidate", "--maintenance"]));

            // Identity has committed its quarantine, while the whole second transaction
            // (including its audit insert before the failing UPDATE) must roll back.
            await using (var identity = database.Context())
            {
                var accounts = await identity.Users.AsNoTracking().ToArrayAsync(token);
                Assert.Equal(2, accounts.Length);
                Assert.All(accounts, account =>
                {
                    Assert.True(account.RevalidationRequired);
                    Assert.False(account.IsOwner);
                    Assert.False(account.UsersManage);
                    Assert.False(account.ContentManage);
                    Assert.False(account.SubscriptionsManage);
                });
                Assert.Empty(await identity.Sessions.ToArrayAsync(token));
                Assert.Equal(2, await identity.Audit.CountAsync(entry => entry.Action == "account.recovery_invalidated", token));
            }
            await using (var subscriptions = Context())
            {
                var unchanged = await subscriptions.Subscriptions.AsNoTracking().SingleAsync(token);
                Assert.Equal(active.Id, unchanged.Id);
                Assert.Equal("active", unchanged.Status);
                Assert.Equal(active.Version, unchanged.Version);
                Assert.Equal(1, await subscriptions.Audit.CountAsync(token));
                Assert.Equal(0, await subscriptions.Audit.CountAsync(entry => entry.Action == "subscription.recovery_suspended", token));
            }
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/identity/me", token)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/subscriptions/me", token)).StatusCode);
            using var oldPassword = await MfaTestClient.Login(client, member.Email!, Password, token);
            Assert.Equal(HttpStatusCode.Unauthorized, oldPassword.StatusCode);

            await using (var failure = Context(true))
                await failure.Database.ExecuteSqlRawAsync(removeFailure, token);
            Assert.Equal(0, await runCli(["recovery-invalidate", "--maintenance"]));
            string suspendedVersion;
            Guid[] subscriptionAuditIds;
            Guid[] identityAuditIds;
            await using (var subscriptions = Context())
            {
                var suspended = await subscriptions.Subscriptions.AsNoTracking().SingleAsync(token);
                Assert.Equal("suspended", suspended.Status);
                Assert.NotEqual(active.Version, suspended.Version);
                suspendedVersion = suspended.Version;
                var recovery = Assert.Single(await subscriptions.Audit.Where(entry => entry.Action == "subscription.recovery_suspended").ToArrayAsync(token));
                Assert.Equal(active.Id, recovery.SubscriptionId);
                Assert.Equal(member.Id, recovery.UserId);
                Assert.Equal("active", recovery.BeforeStatus);
                Assert.Equal("suspended", recovery.AfterStatus);
                subscriptionAuditIds = await subscriptions.Audit.OrderBy(entry => entry.Id).Select(entry => entry.Id).ToArrayAsync(token);
                Assert.Equal(2, subscriptionAuditIds.Length);
            }
            await using (var identity = database.Context())
            {
                identityAuditIds = await identity.Audit.OrderBy(entry => entry.Id).Select(entry => entry.Id).ToArrayAsync(token);
                Assert.Equal(2, await identity.Audit.CountAsync(entry => entry.Action == "account.recovery_invalidated", token));
            }
            Assert.Equal(0, await runCli(["recovery-invalidate", "--maintenance"]));
            await using (var subscriptions = Context())
            {
                Assert.Equal(suspendedVersion, (await subscriptions.Subscriptions.AsNoTracking().SingleAsync(token)).Version);
                Assert.Equal(subscriptionAuditIds, await subscriptions.Audit.OrderBy(entry => entry.Id).Select(entry => entry.Id).ToArrayAsync(token));
            }
            await using (var identity = database.Context())
            {
                Assert.Equal(identityAuditIds, await identity.Audit.OrderBy(entry => entry.Id).Select(entry => entry.Id).ToArrayAsync(token));
                Assert.All(await identity.Users.AsNoTracking().ToArrayAsync(token), account => Assert.True(account.RevalidationRequired));
                Assert.Empty(await identity.Sessions.ToArrayAsync(token));
            }
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/subscriptions/me", token)).StatusCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ConnectionStrings__Database", previousConnection);
            await using var cleanup = Context(true);
            cleanup.Database.SetCommandTimeout(10);
            await cleanup.Database.ExecuteSqlRawAsync(removeFailure, CancellationToken.None);
        }
    }

    private SubscriptionsDbContext Context(bool migration = false)
    {
        var options = new DbContextOptionsBuilder<SubscriptionsDbContext>();
        SubscriptionsRegistration.ConfigureDatabase(options, migration ? database.MigrationConnection : database.RuntimeConnection);
        return new(options.Options);
    }
    private static async Task<ChannelUser> Create(IdentityApiFactory api, string email, CancellationToken token, bool users = false, bool subscriptions = false, bool confirmed = true)
    {
        using var scope = api.Services.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<UserManager<ChannelUser>>();
        var user = new ChannelUser { Id = Guid.NewGuid(), UserName = email, Email = email, DisplayName = "QA Subscriber", EmailConfirmed = confirmed, UsersManage = users, SubscriptionsManage = subscriptions, LockoutEnabled = true };
        Assert.True((await manager.CreateAsync(user, Password)).Succeeded); token.ThrowIfCancellationRequested(); return user;
    }
    private static async Task<HttpResponseMessage> Write<T>(HttpClient client, HttpMethod method, string url, T body, CancellationToken token)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/v1/identity/csrf", token);
        using var request = new HttpRequestMessage(method, url) { Content = JsonContent.Create(body) };
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());
        return await client.SendAsync(request, token);
    }
}
