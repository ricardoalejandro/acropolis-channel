using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Acropolis.Identity.Application;
using Acropolis.Identity.Infrastructure;
using Acropolis.Migrations;
using Acropolis.Platform.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Acropolis.Identity.IntegrationTests;

[Collection("IdentityPostgres")]
public sealed class IdentityTests(IdentityFixture database)
{
    private const string Password = "Acropolis QA secure phrase 2026";
    private const string ChangedPassword = "Acropolis changed phrase 2026";
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task UpgradeRequiresBothHistoriesPreservesPlatformAndRestrictsRuntime()
    {
        await database.ExecuteAsync("DROP SCHEMA IF EXISTS identity CASCADE; DROP SCHEMA IF EXISTS platform CASCADE; CREATE SCHEMA platform AUTHORIZATION acropolis_migrator; GRANT USAGE ON SCHEMA platform TO acropolis_app", Token);
        await new PlatformMigrationRunner().RunAsync(database.MigrationConnection, Token);
        await using var api = new IdentityApiFactory(database);
        using var client = api.Client();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health/ready", Token)).StatusCode);
        await new ChannelMigrationRunner().RunAsync(database.MigrationConnection, Token);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready", Token)).StatusCode);
        await Task.WhenAll(new ChannelMigrationRunner().RunAsync(database.MigrationConnection, Token), new ChannelMigrationRunner().RunAsync(database.MigrationConnection, Token));
        await using var context = database.Context();
        Assert.False(context.Database.HasPendingModelChanges());
        Assert.Single(await context.Database.GetAppliedMigrationsAsync(Token));
        foreach (var sql in new[] { "CREATE TABLE identity.forbidden(id int)", "INSERT INTO identity.\"__EFMigrationsHistory\" VALUES ('forbidden','10')", "DELETE FROM identity.\"Audit\"", "INSERT INTO identity.\"Bootstrap\" VALUES (1,true)" })
        {
            await Assert.ThrowsAsync<Npgsql.PostgresException>(() => context.Database.ExecuteSqlRawAsync(sql, Token));
        }
        await database.ExecuteAsync("INSERT INTO identity.\"__EFMigrationsHistory\" VALUES ('20990101_Unknown','10.0.12')", Token);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health/ready", Token)).StatusCode);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new ChannelMigrationRunner().RunAsync(database.MigrationConnection, Token));
    }

    [Fact]
    public async Task RegistrationIsStrictNonEnumeratingAndSingleUseEvenWithConcurrentDuplicates()
    {
        await database.ResetAsync(Token);
        await using var api = new IdentityApiFactory(database);
        using var first = api.Client();
        using var second = api.Client();
        Assert.Equal(HttpStatusCode.BadRequest, (await first.PostAsJsonAsync("/api/v1/identity/register", new RegisterRequest("Ana", "ana@example.test", Password), Token)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Write(first, HttpMethod.Post, "/api/v1/identity/register", new { displayName = "Ana", email = "ana@example.test", password = Password, permissions = new[] { "Users.Manage" } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Write(first, HttpMethod.Post, "/api/v1/identity/register", new RegisterRequest("", "bad", "short"))).StatusCode);
        var results = await Task.WhenAll(Write(first, HttpMethod.Post, "/api/v1/identity/register", new RegisterRequest("Ana", "ana@example.test", Password)), Write(second, HttpMethod.Post, "/api/v1/identity/register", new RegisterRequest("Ana", "ANA@example.test", Password)));
        Assert.All(results, response => Assert.Equal(HttpStatusCode.Accepted, response.StatusCode));
        Assert.Equal(HttpStatusCode.Unauthorized, (await Write(first, HttpMethod.Post, "/api/v1/identity/login", new LoginRequest("ana@example.test", Password))).StatusCode);
        await using (var context = database.Context())
        {
            Assert.Single(await context.Users.ToArrayAsync(Token));
            Assert.DoesNotContain("ana@example.test", (await context.Outbox.SingleAsync(Token)).Payload, StringComparison.OrdinalIgnoreCase);
        }
        await api.DispatchAsync();
        var confirmation = Assert.Single(api.Mailer.Messages);
        Assert.Equal(HttpStatusCode.BadRequest, (await Write(first, HttpMethod.Post, "/api/v1/identity/confirm-email", new ConfirmEmailRequest(Guid.NewGuid(), confirmation.Token))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Write(first, HttpMethod.Post, "/api/v1/identity/confirm-email", new ConfirmEmailRequest(confirmation.UserId, confirmation.Token))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Write(first, HttpMethod.Post, "/api/v1/identity/confirm-email", new ConfirmEmailRequest(confirmation.UserId, confirmation.Token))).StatusCode);
        using var existing = await Write(first, HttpMethod.Post, "/api/v1/identity/register", new RegisterRequest("Ana", "ana@example.test", Password));
        using var unknown = await Write(first, HttpMethod.Post, "/api/v1/identity/forgot-password", new EmailRequest("missing@example.test"));
        Assert.Equal(HttpStatusCode.Accepted, existing.StatusCode);
        Assert.Equal(await existing.Content.ReadAsStringAsync(Token), await unknown.Content.ReadAsStringAsync(Token));
    }

    [Fact]
    public async Task SessionIsSecureAbsoluteRevocableAndProfileCannotElevatePermissions()
    {
        await database.ResetAsync(Token);
        await using var api = new IdentityApiFactory(database);
        using var client = api.Client();
        var userId = await RegisterConfirmed(api, client, "member@example.test");
        using var login = await Write(client, HttpMethod.Post, "/api/v1/identity/login", new LoginRequest("member@example.test", Password));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var cookie = login.Headers.GetValues("Set-Cookie").Single(value => value.StartsWith("__Host-acropolis-session=", StringComparison.Ordinal));
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("domain=", cookie, StringComparison.OrdinalIgnoreCase);
        var me = await client.GetFromJsonAsync<UserView>("/api/v1/identity/me", Token);
        Assert.Equal(userId, me!.Id);
        Assert.Equal(["Externo"], me.Levels);
        Assert.Empty(me.Permissions);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/admin/users", Token)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Write(client, HttpMethod.Patch, "/api/v1/identity/me", new { displayName = "Ana", levels = new[] { "Instructor" } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Write(client, HttpMethod.Patch, "/api/v1/identity/me", new ProfileRequest(""))).StatusCode);
        using var profile = await Write(client, HttpMethod.Patch, "/api/v1/identity/me", new ProfileRequest("Nombre actualizado"));
        Assert.Equal(HttpStatusCode.OK, profile.StatusCode);
        Assert.Equal("Nombre actualizado", (await profile.Content.ReadFromJsonAsync<UserView>(Token))!.DisplayName);
        api.Clock.Advance(TimeSpan.FromHours(7));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/identity/me", Token)).StatusCode);
        api.Clock.Advance(TimeSpan.FromHours(1).Add(TimeSpan.FromSeconds(1)));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/identity/me", Token)).StatusCode);
        using var loginAgain = await Write(client, HttpMethod.Post, "/api/v1/identity/login", new LoginRequest("member@example.test", Password));
        Assert.Equal(HttpStatusCode.OK, loginAgain.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Write(client, HttpMethod.Post, "/api/v1/identity/change-password", new ChangePasswordRequest("wrong current phrase", ChangedPassword))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Write(client, HttpMethod.Post, "/api/v1/identity/change-password", new ChangePasswordRequest(Password, ChangedPassword))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/identity/me", Token)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Write(client, HttpMethod.Post, "/api/v1/identity/login", new LoginRequest("member@example.test", Password))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Write(client, HttpMethod.Post, "/api/v1/identity/login", new LoginRequest("member@example.test", ChangedPassword))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Write(client, HttpMethod.Post, "/api/v1/identity/logout", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/identity/me", Token)).StatusCode);
    }

    [Fact]
    public async Task ResetAndConfirmationExpireAndResetRevokesAllSessionsAtomically()
    {
        await database.ResetAsync(Token);
        await using var api = new IdentityApiFactory(database);
        using var client = api.Client();
        await Write(client, HttpMethod.Post, "/api/v1/identity/register", new RegisterRequest("Persona", "expiry@example.test", Password));
        await api.DispatchAsync();
        var old = api.Mailer.Messages.Last();
        api.Clock.Advance(TimeSpan.FromHours(25));
        Assert.Equal(HttpStatusCode.BadRequest, (await Write(client, HttpMethod.Post, "/api/v1/identity/confirm-email", new ConfirmEmailRequest(old.UserId, old.Token))).StatusCode);
        await Write(client, HttpMethod.Post, "/api/v1/identity/resend-confirmation", new EmailRequest("expiry@example.test"));
        await api.DispatchAsync();
        var valid = api.Mailer.Messages.Last();
        Assert.NotEqual(old.Token, valid.Token);
        Assert.Equal(HttpStatusCode.NoContent, (await Write(client, HttpMethod.Post, "/api/v1/identity/confirm-email", new ConfirmEmailRequest(valid.UserId, valid.Token))).StatusCode);
        await Write(client, HttpMethod.Post, "/api/v1/identity/login", new LoginRequest(valid.Email, Password));
        using var other = api.Client();
        await Write(other, HttpMethod.Post, "/api/v1/identity/login", new LoginRequest(valid.Email, Password));
        await Write(client, HttpMethod.Post, "/api/v1/identity/forgot-password", new EmailRequest(valid.Email));
        await api.DispatchAsync();
        var reset = api.Mailer.Messages.Last();
        api.Clock.Advance(TimeSpan.FromMinutes(31));
        Assert.Equal(HttpStatusCode.BadRequest, (await Write(client, HttpMethod.Post, "/api/v1/identity/reset-password", new ResetPasswordRequest(reset.UserId, reset.Token, ChangedPassword))).StatusCode);
        await Write(client, HttpMethod.Post, "/api/v1/identity/forgot-password", new EmailRequest(valid.Email));
        await api.DispatchAsync();
        reset = api.Mailer.Messages.Last();
        Assert.Equal(HttpStatusCode.BadRequest, (await Write(client, HttpMethod.Post, "/api/v1/identity/reset-password", new ResetPasswordRequest(reset.UserId, reset.Token, "short"))).StatusCode);
        var races = await Task.WhenAll(Write(client, HttpMethod.Post, "/api/v1/identity/reset-password", new ResetPasswordRequest(reset.UserId, reset.Token, ChangedPassword)), Write(other, HttpMethod.Post, "/api/v1/identity/reset-password", new ResetPasswordRequest(reset.UserId, reset.Token, ChangedPassword)));
        Assert.Single(races, item => item.StatusCode == HttpStatusCode.NoContent);
        Assert.Single(races, item => item.StatusCode == HttpStatusCode.BadRequest);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/identity/me", Token)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await other.GetAsync("/api/v1/identity/me", Token)).StatusCode);
    }

    [Fact]
    public async Task AdminBootstrapConcurrencyLevelsAuditAndLastAdministratorAreEnforced()
    {
        await database.ResetAsync(Token);
        await using var api = new IdentityApiFactory(database);
        using var admin = api.Client();
        var adminId = await RegisterConfirmed(api, admin, "admin@example.test");
        using var member = api.Client();
        var memberId = await RegisterConfirmed(api, member, "user@example.test");
        await using (var context = database.Context(true))
        {
            await new IdentityOperations(context, api.Clock).BootstrapAsync("admin@example.test", Token);
            await Assert.ThrowsAsync<InvalidOperationException>(() => new IdentityOperations(context, api.Clock).BootstrapAsync("user@example.test", Token));
        }
        await Write(admin, HttpMethod.Post, "/api/v1/identity/login", new LoginRequest("admin@example.test", Password));
        await Write(member, HttpMethod.Post, "/api/v1/identity/login", new LoginRequest("user@example.test", Password));
        var page = await admin.GetFromJsonAsync<UserPage>("/api/v1/admin/users?search=user&status=active&level=Externo&page=1&pageSize=10", Token);
        Assert.Equal(1, page!.Total);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync("/api/v1/admin/users?pageSize=101", Token)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/api/v1/admin/users/" + Guid.NewGuid(), Token)).StatusCode);
        var user = await admin.GetFromJsonAsync<UserView>("/api/v1/admin/users/" + memberId, Token);
        Assert.Equal(HttpStatusCode.Conflict, (await Write(admin, HttpMethod.Patch, "/api/v1/admin/users/" + memberId, new AdminUserRequest("stale", Levels: ["Instructor"]))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Write(admin, HttpMethod.Patch, "/api/v1/admin/users/" + memberId, new { version = user!.Version, usersManage = true })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Write(admin, HttpMethod.Patch, "/api/v1/admin/users/" + memberId, new AdminUserRequest(user!.Version, Levels: ["Miembro", "Instructor"]))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await member.GetAsync("/api/v1/identity/me", Token)).StatusCode);
        user = await admin.GetFromJsonAsync<UserView>("/api/v1/admin/users/" + memberId, Token);
        Assert.Equal(HttpStatusCode.OK, (await Write(admin, HttpMethod.Patch, "/api/v1/admin/users/" + memberId, new AdminUserRequest(user!.Version, Status: "disabled"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Write(member, HttpMethod.Post, "/api/v1/identity/login", new LoginRequest("user@example.test", Password))).StatusCode);
        user = await admin.GetFromJsonAsync<UserView>("/api/v1/admin/users/" + memberId, Token);
        Assert.Equal(HttpStatusCode.OK, (await Write(admin, HttpMethod.Patch, "/api/v1/admin/users/" + memberId, new AdminUserRequest(user!.Version, "Otro nombre", "active", []))).StatusCode);
        var own = await admin.GetFromJsonAsync<UserView>("/api/v1/identity/me", Token);
        Assert.Equal(HttpStatusCode.Conflict, (await Write(admin, HttpMethod.Patch, "/api/v1/admin/users/" + adminId, new AdminUserRequest(own!.Version, Status: "disabled"))).StatusCode);
        await using var audit = database.Context();
        Assert.Equal(4, await audit.Audit.CountAsync(Token));
        Assert.False((await audit.Users.SingleAsync(x => x.Id == memberId, Token)).UsersManage);
    }

    [Fact]
    public async Task OutboxHasDurableLeaseCooldownRetryAndCancelledFlowsCannotBeDelivered()
    {
        await database.ResetAsync(Token);
        await using var api = new IdentityApiFactory(database);
        using var client = api.Client();
        await Write(client, HttpMethod.Post, "/api/v1/identity/register", new RegisterRequest("Persona", "outbox@example.test", Password));
        api.Mailer.Fail = true;
        await api.DispatchAsync();
        await using (var context = database.Context())
        {
            var item = await context.Outbox.SingleAsync(Token);
            Assert.Equal("pending", item.Status);
            Assert.Equal(1, item.Attempts);
            Assert.Null(item.LeaseOwner);
        }
        await Write(client, HttpMethod.Post, "/api/v1/identity/resend-confirmation", new EmailRequest("outbox@example.test"));
        await using (var context = database.Context()) Assert.Single(await context.Flows.ToArrayAsync(Token));
        api.Clock.Advance(TimeSpan.FromSeconds(11));
        api.Mailer.Fail = false;
        await Task.WhenAll(api.DispatchAsync(), api.DispatchAsync());
        Assert.Single(api.Mailer.Messages);
        await Write(client, HttpMethod.Post, "/api/v1/identity/resend-confirmation", new EmailRequest("outbox@example.test"));
        await using (var context = database.Context()) Assert.Single(await context.Flows.ToArrayAsync(Token));
        api.Clock.Advance(TimeSpan.FromSeconds(60));
        await Write(client, HttpMethod.Post, "/api/v1/identity/resend-confirmation", new EmailRequest("outbox@example.test"));
        await using (var context = database.Context())
        {
            var pending = await context.Outbox.SingleAsync(x => x.Status == "pending", Token);
            pending.Status = "sending"; pending.LeaseOwner = "lost-worker"; pending.LeaseExpiresUtc = api.Clock.GetUtcNow().AddSeconds(-1);
            await context.SaveChangesAsync(Token);
        }
        await api.DispatchAsync();
        Assert.Equal(2, api.Mailer.Messages.Count);
        api.Clock.Advance(TimeSpan.FromSeconds(60));
        await Write(client, HttpMethod.Post, "/api/v1/identity/resend-confirmation", new EmailRequest("outbox@example.test"));
        api.Clock.Advance(TimeSpan.FromHours(25));
        await api.DispatchAsync();
        Assert.Equal(2, api.Mailer.Messages.Count);
        await using var check = database.Context();
        Assert.Contains(await check.Outbox.Select(x => x.Status).ToArrayAsync(Token), item => item == "cancelled");
    }

    [Fact]
    public async Task RecoveryQuarantinesAccountsAndNeverRestoresOldPasswordsOrPrivileges()
    {
        await database.ResetAsync(Token);
        await using var api = new IdentityApiFactory(database);
        using var client = api.Client();
        var id = await RegisterConfirmed(api, client, "recover@example.test");
        await Write(client, HttpMethod.Post, "/api/v1/identity/login", new LoginRequest("recover@example.test", Password));
        await using (var context = database.Context(true))
        {
            var operations = new IdentityOperations(context, api.Clock);
            await operations.InvalidateRecoveryAsync(Token);
            await operations.RevalidateAsync("recover@example.test", false, Token);
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/identity/me", Token)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Write(client, HttpMethod.Post, "/api/v1/identity/login", new LoginRequest("recover@example.test", Password))).StatusCode);
        await using var verify = database.Context();
        var user = await verify.Users.SingleAsync(x => x.Id == id, Token);
        Assert.Null(user.PasswordHash);
        Assert.False(user.EmailConfirmed);
        Assert.False(user.UsersManage);
        Assert.Empty(await verify.Sessions.ToArrayAsync(Token));
    }


    [Fact]
    public async Task StoredTicketsCannotExtendExpiryAndRejectCorruptionOrChangedAuthorization()
    {
        await database.ResetAsync(Token);
        await using var api = new IdentityApiFactory(database);
        using var client = api.Client();
        var id = await RegisterConfirmed(api, client, "tickets@example.test");
        await using var context = database.Context();
        var user = await context.Users.SingleAsync(x => x.Id == id, Token);
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, id.ToString()), new Claim("auth_version", user.SecurityVersion) }, "Identity.Application"));
        var ticket = new AuthenticationTicket(principal, new AuthenticationProperties { ExpiresUtc = api.Clock.GetUtcNow().AddHours(16) }, "Identity.Application");
        var store = api.Services.GetRequiredService<ITicketStore>();
        var key = await store.StoreAsync(ticket, TestContext.Current.CancellationToken);
        Assert.NotNull(await store.RetrieveAsync(key, TestContext.Current.CancellationToken));
        Assert.Null(await store.RetrieveAsync("", TestContext.Current.CancellationToken));
        Assert.Null(await store.RetrieveAsync(new string('x', 257), TestContext.Current.CancellationToken));
        Assert.Null(await store.RetrieveAsync("missing", TestContext.Current.CancellationToken));
        await store.RenewAsync("missing", ticket, TestContext.Current.CancellationToken);
        var expiry = (await context.Sessions.SingleAsync(Token)).ExpiresUtc;
        ticket.Properties.ExpiresUtc = api.Clock.GetUtcNow().AddHours(20);
        await store.RenewAsync(key, ticket, TestContext.Current.CancellationToken);
        Assert.Equal(expiry, (await context.Sessions.AsNoTracking().SingleAsync(Token)).ExpiresUtc);
        var row = await context.Sessions.SingleAsync(Token);
        row.Ticket = [0, 1, 2, 3];
        await context.SaveChangesAsync(Token);
        Assert.Null(await store.RetrieveAsync(key, TestContext.Current.CancellationToken));
        await store.RemoveAsync(key, TestContext.Current.CancellationToken);
        Assert.Null(await store.RetrieveAsync(key, TestContext.Current.CancellationToken));
        key = await store.StoreAsync(ticket, TestContext.Current.CancellationToken);
        user.SecurityVersion = Guid.NewGuid().ToString("N");
        await context.SaveChangesAsync(Token);
        Assert.Null(await store.RetrieveAsync(key, TestContext.Current.CancellationToken));
        await store.RemoveAsync(key, TestContext.Current.CancellationToken);
        ticket = new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, id.ToString()), new Claim("auth_version", user.SecurityVersion) }, "Identity.Application")), ticket.Properties, "Identity.Application");
        key = await store.StoreAsync(ticket, TestContext.Current.CancellationToken);
        api.Clock.Advance(TimeSpan.FromHours(8).Add(TimeSpan.FromSeconds(1)));
        Assert.Null(await store.RetrieveAsync(key, TestContext.Current.CancellationToken));
        await store.RenewAsync(key, ticket, TestContext.Current.CancellationToken);
        Assert.Null(await store.RetrieveAsync(key, TestContext.Current.CancellationToken));
        await using var migration = database.Context(true);
        Assert.True(await new IdentityOperations(migration, api.Clock).PruneAsync(Token) > 0);
    }

    [Fact]
    public async Task BootstrapAndLastAdministratorChangesHaveOneWinnerUnderConcurrency()
    {
        await database.ResetAsync(Token);
        await using var api = new IdentityApiFactory(database);
        using var one = api.Client();
        using var two = api.Client();
        var first = await RegisterConfirmed(api, one, "one@example.test");
        var second = await RegisterConfirmed(api, two, "two@example.test");
        async Task<bool> Bootstrap(string email)
        {
            await using var context = database.Context(true);
            try { await new IdentityOperations(context, api.Clock).BootstrapAsync(email, Token); return true; }
            catch (InvalidOperationException) { return false; }
        }
        var winners = await Task.WhenAll(Bootstrap("one@example.test"), Bootstrap("two@example.test"));
        Assert.Single(winners, result => result);
        await using (var context = database.Context(true))
        {
            var users = await context.Users.ToArrayAsync(Token);
            foreach (var user in users) user.UsersManage = true; // Synthetic second administrator for the concurrency invariant.
            await context.SaveChangesAsync(Token);
        }
        await Write(one, HttpMethod.Post, "/api/v1/identity/login", new LoginRequest("one@example.test", Password));
        await Write(two, HttpMethod.Post, "/api/v1/identity/login", new LoginRequest("two@example.test", Password));
        var a = await one.GetFromJsonAsync<UserView>("/api/v1/identity/me", Token);
        var b = await two.GetFromJsonAsync<UserView>("/api/v1/identity/me", Token);
        var results = await Task.WhenAll(Write(one, HttpMethod.Patch, "/api/v1/admin/users/" + first, new AdminUserRequest(a!.Version, Status: "disabled")), Write(two, HttpMethod.Patch, "/api/v1/admin/users/" + second, new AdminUserRequest(b!.Version, Status: "disabled")));
        Assert.Single(results, response => response.StatusCode == HttpStatusCode.OK);
        Assert.Single(results, response => response.StatusCode == HttpStatusCode.Conflict);
        await using var check = database.Context();
        Assert.Equal(1, await check.Users.CountAsync(x => x.UsersManage && !x.IsDisabled, Token));
    }

    [Fact]
    public async Task ValidationLockoutAndExplicitRecoveryRemainNonEnumerating()
    {
        await database.ResetAsync(Token);
        await using var api = new IdentityApiFactory(database);
        using var client = api.Client();
        var id = await RegisterConfirmed(api, client, "limits@example.test");
        for (var attempt = 0; attempt < 6; attempt++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await Write(client, HttpMethod.Post, "/api/v1/identity/login", new LoginRequest("limits@example.test", "Incorrect password phrase"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Write(client, HttpMethod.Post, "/api/v1/identity/login", new LoginRequest("limits@example.test", Password))).StatusCode);
        using var missing = await Write(client, HttpMethod.Post, "/api/v1/identity/login", new LoginRequest("missing@example.test", Password));
        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
        api.Clock.Advance(TimeSpan.FromMinutes(16));
        await using (var scope = api.Services.CreateAsyncScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<IIdentityService>();
            Assert.Null(await service.GetUserAsync(Guid.NewGuid(), Token));
            Assert.Equal("invalid_credentials", (await service.UpdateProfileAsync(Guid.NewGuid(), new ProfileRequest("Valid name"), Token)).Error);
            Assert.Equal("validation_error", (await service.ChangePasswordAsync(id, new ChangePasswordRequest("short", Password), Token)).Error);
            Assert.Equal("invalid_credentials", (await service.ChangePasswordAsync(Guid.NewGuid(), new ChangePasswordRequest(Password, Password), Token)).Error);
            Assert.Equal("forbidden", (await service.UpdateUserAsync(id, id, new AdminUserRequest("version", "Valid name"), Token)).Error);
            Assert.Equal("validation_error", (await service.UpdateUserAsync(id, id, new AdminUserRequest("", Status: "pending", Levels: ["invalid"]), Token)).Error);
            Assert.Equal("invalid_token", (await service.ConfirmEmailAsync(new ConfirmEmailRequest(Guid.NewGuid(), ""), Token)).Error);
            Assert.Equal("invalid_token", (await service.ResetPasswordAsync(new ResetPasswordRequest(id, new string('x', 257), Password), Token)).Error);
            await service.RequestEmailAsync("invalid", "confirm", Token);
            await service.RequestEmailAsync("limits@example.test", "unknown-purpose", Token);
            await service.RequestEmailAsync("limits@example.test", "confirm", Token);
        }
        await using (var context = database.Context(true))
        {
            await new IdentityOperations(context, api.Clock).InvalidateRecoveryAsync(Token);
            await new IdentityOperations(context, api.Clock).RevalidateAsync("limits@example.test", true, Token);
            await Assert.ThrowsAsync<InvalidOperationException>(() => new IdentityOperations(context, api.Clock).RevalidateAsync("limits@example.test", false, Token));
        }
        await Write(client, HttpMethod.Post, "/api/v1/identity/resend-confirmation", new EmailRequest("limits@example.test"));
        await api.DispatchAsync();
        var confirmation = api.Mailer.Messages.Last();
        await Write(client, HttpMethod.Post, "/api/v1/identity/confirm-email", new ConfirmEmailRequest(id, confirmation.Token));
        await Write(client, HttpMethod.Post, "/api/v1/identity/forgot-password", new EmailRequest("limits@example.test"));
        await api.DispatchAsync();
        var reset = api.Mailer.Messages.Last();
        Assert.Equal(HttpStatusCode.NoContent, (await Write(client, HttpMethod.Post, "/api/v1/identity/reset-password", new ResetPasswordRequest(id, reset.Token, ChangedPassword))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Write(client, HttpMethod.Post, "/api/v1/identity/login", new LoginRequest("limits@example.test", Password))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Write(client, HttpMethod.Post, "/api/v1/identity/login", new LoginRequest("limits@example.test", ChangedPassword))).StatusCode);
    }

    [Fact]
    public async Task OutboxStopsRetryingAndPruningPreservesActiveFlows()
    {
        await database.ResetAsync(Token);
        await using var api = new IdentityApiFactory(database);
        using var client = api.Client();
        await Write(client, HttpMethod.Post, "/api/v1/identity/register", new RegisterRequest("Persona", "failed@example.test", Password));
        api.Mailer.Fail = true;
        for (var index = 0; index < 5; index++) { await api.DispatchAsync(); api.Clock.Advance(TimeSpan.FromMinutes(11)); }
        await using (var context = database.Context())
        {
            var item = await context.Outbox.SingleAsync(Token);
            Assert.Equal("failed", item.Status);
            Assert.Equal(5, item.Attempts);
            Assert.Empty(item.Payload);
        }
        api.Mailer.Fail = false;
        await Write(client, HttpMethod.Post, "/api/v1/identity/resend-confirmation", new EmailRequest("failed@example.test"));
        using (var worker = new OutboxWorker(api.Services.GetRequiredService<IServiceScopeFactory>(), Microsoft.Extensions.Logging.Abstractions.NullLogger<OutboxWorker>.Instance))
        {
            await worker.StartAsync(Token);
            await Task.Delay(TimeSpan.FromSeconds(3), Token);
            await worker.StopAsync(Token);
        }
        Assert.Single(api.Mailer.Messages);
        await using var migration = database.Context(true);
        Assert.Equal(0, await new IdentityOperations(migration, api.Clock).PruneAsync(Token));
        api.Clock.Advance(TimeSpan.FromDays(9));
        Assert.True(await new IdentityOperations(migration, api.Clock).PruneAsync(Token) > 0);
        Assert.Empty(await migration.Outbox.ToArrayAsync(Token));
    }

    [Fact]
    public async Task PendingAccountCanBeReenabledAndForeignOriginCannotWrite()
    {
        await database.ResetAsync(Token);
        await using var api = new IdentityApiFactory(database);
        using var admin = api.Client();
        await RegisterConfirmed(api, admin, "pending-admin@example.test");
        await using (var context = database.Context(true))
            await new IdentityOperations(context, api.Clock).BootstrapAsync("pending-admin@example.test", Token);
        await Write(admin, HttpMethod.Post, "/api/v1/identity/login", new LoginRequest("pending-admin@example.test", Password));
        using var pending = api.Client();
        await Write(pending, HttpMethod.Post, "/api/v1/identity/register", new RegisterRequest("Persona", "pending-user@example.test", Password));
        await using var lookup = database.Context();
        var id = (await lookup.Users.SingleAsync(x => x.Email == "pending-user@example.test", Token)).Id;
        var user = await admin.GetFromJsonAsync<UserView>("/api/v1/admin/users/" + id, Token);
        Assert.Equal("pending", user!.Status);
        var disabled = await Write(admin, HttpMethod.Patch, "/api/v1/admin/users/" + id, new AdminUserRequest(user.Version, Status: "disabled"));
        Assert.Equal("disabled", (await disabled.Content.ReadFromJsonAsync<UserView>(Token))!.Status);
        user = await admin.GetFromJsonAsync<UserView>("/api/v1/admin/users/" + id, Token);
        var enabled = await Write(admin, HttpMethod.Patch, "/api/v1/admin/users/" + id, new AdminUserRequest(user!.Version, Status: "active"));
        user = await enabled.Content.ReadFromJsonAsync<UserView>(Token);
        Assert.Equal("pending", user!.Status);
        Assert.False(user.EmailConfirmed);
        api.Clock.Advance(TimeSpan.FromSeconds(61));
        await Write(pending, HttpMethod.Post, "/api/v1/identity/resend-confirmation", new EmailRequest(user.Email));
        await api.DispatchAsync();
        var flow = api.Mailer.Messages.Last(x => x.Email == user.Email);
        Assert.Equal(HttpStatusCode.NoContent, (await Write(pending, HttpMethod.Post, "/api/v1/identity/confirm-email", new ConfirmEmailRequest(id, flow.Token))).StatusCode);
        var csrf = await pending.GetFromJsonAsync<JsonElement>("/api/v1/identity/csrf", Token);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/identity/login") { Content = JsonContent.Create(new LoginRequest(user.Email, Password)) };
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());
        request.Headers.Add("Origin", "https://other.example.test");
        Assert.Equal(HttpStatusCode.BadRequest, (await pending.SendAsync(request, Token)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Write(pending, HttpMethod.Post, "/api/v1/identity/login", new LoginRequest(user.Email, Password))).StatusCode);
        await using var owner = database.Context(true);
        var target = await owner.Users.SingleAsync(x => x.Id == id, Token);
        target.RevalidationRequired = true;
        await owner.SaveChangesAsync(Token);
        user = await admin.GetFromJsonAsync<UserView>("/api/v1/admin/users/" + id, Token);
        await using var scope = api.Services.CreateAsyncScope();
        Assert.Equal("account_unconfirmed", (await scope.ServiceProvider.GetRequiredService<IIdentityService>().UpdateUserAsync(
            (await owner.Users.SingleAsync(x => x.Email == "pending-admin@example.test", Token)).Id, id, new AdminUserRequest(user!.Version, Status: "active"), Token)).Error);
    }

    private static async Task<Guid> RegisterConfirmed(IdentityApiFactory api, HttpClient client, string email)
    {
        Assert.Equal(HttpStatusCode.Accepted, (await Write(client, HttpMethod.Post, "/api/v1/identity/register", new RegisterRequest("Persona", email, Password))).StatusCode);
        await api.DispatchAsync();
        var flow = api.Mailer.Messages.Last(x => x.Email == email);
        Assert.Equal(HttpStatusCode.NoContent, (await Write(client, HttpMethod.Post, "/api/v1/identity/confirm-email", new ConfirmEmailRequest(flow.UserId, flow.Token))).StatusCode);
        return flow.UserId;
    }
    private static async Task<HttpResponseMessage> Write<T>(HttpClient client, HttpMethod method, string path, T body)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/v1/identity/csrf", Token);
        using var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body) };
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());
        return await client.SendAsync(request, Token);
    }
}
