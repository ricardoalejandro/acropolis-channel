using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Acropolis.Identity.Application;
using Acropolis.Identity.Infrastructure;
using Acropolis.Migrations;
using Acropolis.TestSupport;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace Acropolis.Identity.IntegrationTests;

[Collection("IdentityPostgres")]
public sealed class AccountAccessTests(IdentityFixture database)
{
    private const string Password = "Account access isolated QA secure phrase 2026";
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private readonly IDataProtectionProvider protection = new EphemeralDataProtectionProvider();

    [Fact]
    public async Task UpgradeCreatesAnEmptyAccessTableWithoutInferringOldSessionOrUserDates()
    {
        await database.ResetAsync(Token);
        await using var migration = database.Context(true);
        await migration.GetService<IMigrator>().MigrateAsync("20261006074206_AddProtectedOwner", Token);
        var user = User("access-legacy");
        migration.Users.Add(user);
        migration.Sessions.Add(new StoredSession
        {
            Id = new string('a', 64),
            UserId = user.Id,
            SecurityVersion = user.SecurityVersion,
            Ticket = [1],
            CreatedUtc = DateTimeOffset.UtcNow.AddDays(-1),
            ExpiresUtc = DateTimeOffset.UtcNow.AddHours(1)
        });
        await migration.SaveChangesAsync(Token);
        var xmin = await UserXmin(migration, user.Id);
        await new ChannelMigrationRunner().RunAsync(database.MigrationConnection, Token);
        await new ChannelMigrationRunner().RunAsync(database.MigrationConnection, Token);
        Assert.False(migration.Database.HasPendingModelChanges());
        Assert.Empty(await migration.AccountAccess.ToArrayAsync(Token));
        Assert.Equal(xmin, await UserXmin(migration, user.Id));
        Assert.Single(await migration.Sessions.ToArrayAsync(Token));
        var access = await new AccountAccessService(migration).GetAsync(user.Id, Token);
        Assert.NotNull(access);
        Assert.Null(access.LastSignInUtc);
    }

    [Fact]
    public async Task ValidTicketsRecordOnlyTheMaximumUtcWithoutRewritingUserVersionOrAudit()
    {
        await database.ResetAsync(Token);
        await using var context = database.Context();
        var user = User("access-monotonic"); context.Users.Add(user); await context.SaveChangesAsync(Token);
        var xmin = await UserXmin(context, user.Id);
        var version = user.ConcurrencyStamp; var security = user.SecurityVersion;
        var clock = new TestClock(); var earlier = clock.GetUtcNow();
        var store = Store(clock); var first = await store.StoreAsync(Ticket(user, clock), Token);
        Assert.NotNull(await store.RetrieveAsync(first, Token));
        await using (var read = database.Context())
            Assert.Equal(PgUtc(earlier), (await read.AccountAccess.SingleAsync(Token)).LastSignInUtc);
        clock.Advance(TimeSpan.FromMinutes(1)); var latest = clock.GetUtcNow();
        var tickets = await Task.WhenAll(store.StoreAsync(Ticket(user, clock), Token), store.StoreAsync(Ticket(user, clock), Token));
        Assert.Equal(2, tickets.Distinct().Count());
        var olderClock = new TestClock();
        olderClock.Advance(earlier - olderClock.GetUtcNow());
        await Store(olderClock).StoreAsync(Ticket(user, olderClock), Token);
        await using var final = database.Context();
        var access = Assert.Single(await final.AccountAccess.ToArrayAsync(Token));
        Assert.Equal(PgUtc(latest), access.LastSignInUtc); Assert.Equal(TimeSpan.Zero, access.LastSignInUtc.Offset);
        var record = await final.Users.AsNoTracking().SingleAsync(Token);
        Assert.Equal(version, record.ConcurrencyStamp); Assert.Equal(security, record.SecurityVersion);
        Assert.Equal(xmin, await UserXmin(final, user.Id)); Assert.Empty(await final.Audit.ToArrayAsync(Token));
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("stale")]
    [InlineData("pending")]
    [InlineData("disabled")]
    [InlineData("revalidation")]
    [InlineData("mfa-password")]
    [InlineData("admin-password")]
    [InlineData("no-proof")]
    [InlineData("mfa-disabled")]
    [InlineData("admin-mfa-disabled")]
    public async Task AlreadyInvalidOrIncompleteTicketsRetainStorageContractWithoutRecordingAccess(string reason)
    {
        await database.ResetAsync(Token); var user = User("access-ineligible");
        if (reason == "pending") user.EmailConfirmed = false;
        if (reason == "disabled") user.IsDisabled = true;
        if (reason == "revalidation") user.RevalidationRequired = true;
        if (reason == "mfa-password") user.TwoFactorEnabled = true;
        if (reason is "admin-password" or "admin-mfa-disabled") user.UsersManage = true;
        await using (var context = database.Context()) { context.Users.Add(user); await context.SaveChangesAsync(Token); }
        var clock = new TestClock(); var method = reason == "no-proof" ? null : reason is "mfa-disabled" or "admin-mfa-disabled" ? "mfa" : "pwd";
        var ticket = Ticket(user, clock, method);
        if (reason == "expired") ticket.Properties.ExpiresUtc = clock.GetUtcNow().AddSeconds(-1);
        if (reason == "stale") ((ClaimsIdentity)ticket.Principal.Identity!).RemoveClaim(ticket.Principal.FindFirst("auth_version")!);
        if (reason == "stale") ((ClaimsIdentity)ticket.Principal.Identity!).AddClaim(new("auth_version", Guid.NewGuid().ToString("N")));
        var key = await Store(clock).StoreAsync(ticket, Token);
        Assert.NotEmpty(key);
        await using var verify = database.Context();
        Assert.Single(await verify.Sessions.ToArrayAsync(Token));
        Assert.Empty(await verify.AccountAccess.ToArrayAsync(Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailureOrCancellationBetweenSessionAndAccessRollsBackBothAndPreservesExistingState(bool cancel)
    {
        await database.ResetAsync(Token); var clock = new TestClock(); var user = User("access-rollback");
        await using (var context = database.Context()) { context.Users.Add(user); await context.SaveChangesAsync(Token); }
        var original = await Store(clock).StoreAsync(Ticket(user, clock), Token); var recorded = clock.GetUtcNow();
        clock.Advance(TimeSpan.FromMinutes(1));
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var fail = new BeforeAccess(cancel ? () => { cancelled.Cancel(); cancelled.Token.ThrowIfCancellationRequested(); }
        : () => throw new InvalidOperationException("Synthetic access failure after session persistence"));
        var failing = Store(clock, fail);
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => failing.StoreAsync(Ticket(user, clock), cancelled.Token));
        else await Assert.ThrowsAsync<InvalidOperationException>(() => failing.StoreAsync(Ticket(user, clock), Token));
        Assert.Equal(1, fail.Calls);
        await using var verify = database.Context();
        Assert.Single(await verify.Sessions.ToArrayAsync(Token));
        Assert.Equal(PgUtc(recorded), (await verify.AccountAccess.SingleAsync(Token)).LastSignInUtc);
        Assert.NotNull(await Store(clock).RetrieveAsync(original, Token));
        Assert.Equal(3, verify.Database.GetCommandTimeout());
    }

    [Fact]
    public async Task ContextOverloadHonorsRequestAbortedAndTokenAwareStoreAcceptsStorageContract()
    {
        await database.ResetAsync(Token); var user = User("access-context"); var clock = new TestClock();
        await using (var context = database.Context()) { context.Users.Add(user); await context.SaveChangesAsync(Token); }
        using var aborted = CancellationTokenSource.CreateLinkedTokenSource(Token); aborted.Cancel();
        var http = new DefaultHttpContext { RequestAborted = aborted.Token };
        ITicketStore store = Store(clock);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.StoreAsync(Ticket(user, clock), http, Token));
        await using (var read = database.Context()) { Assert.Empty(await read.Sessions.ToArrayAsync(Token)); Assert.Empty(await read.AccountAccess.ToArrayAsync(Token)); }
        Assert.NotEmpty(await store.StoreAsync(Ticket(user, clock), Token));
    }

    [Fact]
    public async Task RenewRetrieveLogoutPruningAndRecoveryDoNotCreateNewAccessTimes()
    {
        await database.ResetAsync(Token); var user = User("access-lifecycle"); var clock = new TestClock();
        await using (var context = database.Context()) { context.Users.Add(user); await context.SaveChangesAsync(Token); }
        var store = Store(clock); var ticket = Ticket(user, clock); var key = await store.StoreAsync(ticket, Token); var recorded = clock.GetUtcNow();
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.NotNull(await store.RetrieveAsync(key, Token)); await store.RenewAsync(key, ticket, Token);
        await store.RemoveAsync(key, Token);
        await using var maintenance = database.Context(true);
        await new IdentityOperations(maintenance, clock).PruneAsync(Token);
        Assert.Equal(PgUtc(recorded), (await maintenance.AccountAccess.SingleAsync(Token)).LastSignInUtc);
        await new IdentityOperations(maintenance, clock).InvalidateRecoveryAsync(Token);
        Assert.Empty(await maintenance.Sessions.ToArrayAsync(Token));
        Assert.True((await maintenance.Users.AsNoTracking().SingleAsync(Token)).RevalidationRequired);
        Assert.Equal(PgUtc(recorded), (await maintenance.AccountAccess.AsNoTracking().SingleAsync(Token)).LastSignInUtc);
        await new IdentityOperations(maintenance, clock).RevalidateAsync(user.Email!, false, Token);
        Assert.Null((await maintenance.Users.AsNoTracking().SingleAsync(Token)).PasswordHash);
        Assert.Equal(PgUtc(recorded), (await maintenance.AccountAccess.AsNoTracking().SingleAsync(Token)).LastSignInUtc);
    }

    [Fact]
    public async Task RegistrationConfirmationAndInvalidCredentialsStayUnknownUntilRealPasswordSession()
    {
        await database.ResetAsync(Token); await using var api = new IdentityApiFactory(database); using var client = api.Client();
        const string email = "access-password@example.test";
        using var register = await MfaTestClient.Write(client, "register", new RegisterRequest("Synthetic account", email, Password), Token);
        Assert.Equal(HttpStatusCode.Accepted, register.StatusCode); await api.DispatchAsync();
        var mail = Assert.Single(api.Mailer.Messages);
        await using (var read = database.Context()) Assert.Empty(await read.AccountAccess.ToArrayAsync(Token));
        using var confirmed = await MfaTestClient.Write(client, "confirm-email", new ConfirmEmailRequest(mail.UserId, mail.Token), Token);
        Assert.Equal(HttpStatusCode.NoContent, confirmed.StatusCode);
        using var incorrect = await MfaTestClient.Write(client, "login", new LoginRequest(email, "An incorrect synthetic password phrase"), Token);
        Assert.Equal(HttpStatusCode.Unauthorized, incorrect.StatusCode);
        await using (var read = database.Context()) Assert.Empty(await read.AccountAccess.ToArrayAsync(Token));
        using var success = await MfaTestClient.Write(client, "login", new LoginRequest(email, Password), Token);
        Assert.Equal(HttpStatusCode.OK, success.StatusCode);
        await using var verify = database.Context(); var access = await verify.AccountAccess.SingleAsync(Token);
        Assert.Equal(mail.UserId, access.UserId); Assert.Equal(PgUtc(api.Clock.GetUtcNow()), access.LastSignInUtc);
        Assert.Equal(access.LastSignInUtc, (await verify.Sessions.SingleAsync(Token)).CreatedUtc);
        var body = await success.Content.ReadAsStringAsync(Token);
        Assert.DoesNotContain("lastSignInUtc", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RealMfaEnrollmentAndRecoveryRecordOnlyCompletedNewSessions()
    {
        await database.ResetAsync(Token); await using var api = new IdentityApiFactory(database); using var client = api.Client();
        var id = await Create(api, "access-mfa@example.test", administrator: true);
        using var login = await MfaTestClient.Write(client, "login", new LoginRequest("access-mfa@example.test", Password), Token);
        Assert.Equal(HttpStatusCode.Accepted, login.StatusCode);
        var challenge = (await login.Content.ReadFromJsonAsync<MfaChallengeView>(Token))!;
        using var start = await MfaTestClient.Write(client, "mfa/enrollment", new MfaEnrollmentRequest(ChallengeToken: challenge.ChallengeToken), Token);
        var enrollment = (await start.Content.ReadFromJsonAsync<MfaEnrollmentView>(Token))!;
        using var bad = await MfaTestClient.Write(client, "mfa/enable", new MfaEnableRequest(enrollment.ChallengeToken, "invalid"), Token);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        await using (var read = database.Context()) { Assert.Empty(await read.Sessions.ToArrayAsync(Token)); Assert.Empty(await read.AccountAccess.ToArrayAsync(Token)); }
        api.Clock.Advance(TimeSpan.FromMinutes(1)); var first = api.Clock.GetUtcNow();
        using var enabled = await MfaTestClient.Write(client, "mfa/enable", new MfaEnableRequest(enrollment.ChallengeToken, MfaTestClient.FreshCode(enrollment.SharedKey)), Token);
        Assert.Equal(HttpStatusCode.OK, enabled.StatusCode);
        var body = await enabled.Content.ReadFromJsonAsync<JsonElement>(Token); var code = body.GetProperty("recoveryCodes")[0].GetString()!;
        using var logout = await MfaTestClient.Write(client, "logout", new { }, Token); Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        api.Clock.Advance(TimeSpan.FromMinutes(1)); var last = api.Clock.GetUtcNow();
        using var retry = await MfaTestClient.Write(client, "login", new LoginRequest("access-mfa@example.test", Password), Token);
        var verifyChallenge = (await retry.Content.ReadFromJsonAsync<MfaChallengeView>(Token))!;
        await using (var read = database.Context()) Assert.Equal(PgUtc(first), (await read.AccountAccess.SingleAsync(Token)).LastSignInUtc);
        using var proof = await MfaTestClient.Write(client, "mfa/challenge", new MfaVerifyRequest(verifyChallenge.ChallengeToken, RecoveryCode: code), Token);
        Assert.Equal(HttpStatusCode.OK, proof.StatusCode);
        await using var final = database.Context(); var access = await final.AccountAccess.SingleAsync(Token);
        Assert.Equal(id, access.UserId); Assert.Equal(PgUtc(last), access.LastSignInUtc);
    }

    [Fact]
    public async Task AuthenticatedMfaEnrollmentRecordsItsCompletedReplacementAndRejectsTheCopiedOldCookie()
    {
        await database.ResetAsync(Token); await using var api = new IdentityApiFactory(database); using var client = api.Client();
        var id = await Create(api, "access-upgrade@example.test");
        using var login = await MfaTestClient.Write(client, "login", new LoginRequest("access-upgrade@example.test", Password), Token);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode); var recorded = api.Clock.GetUtcNow();
        var oldCookie = login.Headers.GetValues("Set-Cookie").Single(x => x.StartsWith("__Host-acropolis-session=", StringComparison.Ordinal)).Split(';')[0];
        using var copied = api.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), HandleCookies = false, AllowAutoRedirect = false });
        copied.DefaultRequestHeaders.Add("Cookie", oldCookie);
        api.Clock.Advance(TimeSpan.FromMinutes(1));
        using var start = await MfaTestClient.Write(client, "mfa/enrollment", new MfaEnrollmentRequest(CurrentPassword: Password), Token);
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        var enrollment = (await start.Content.ReadFromJsonAsync<MfaEnrollmentView>(Token))!;
        await using (var read = database.Context()) Assert.Equal(PgUtc(recorded), (await read.AccountAccess.SingleAsync(Token)).LastSignInUtc);
        using var enabled = await MfaTestClient.Write(client, "mfa/enable", new MfaEnableRequest(enrollment.ChallengeToken, MfaTestClient.FreshCode(enrollment.SharedKey)), Token);
        Assert.Equal(HttpStatusCode.OK, enabled.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await copied.GetAsync("/api/v1/identity/me", Token)).StatusCode);
        await using var final = database.Context();
        Assert.Equal(id, (await final.Sessions.SingleAsync(Token)).UserId);
        Assert.Equal(PgUtc(api.Clock.GetUtcNow()), (await final.AccountAccess.SingleAsync(Token)).LastSignInUtc);
    }

    [Fact]
    public async Task SigningInAnotherAccountUpdatesTheCorrectAccountAndKeepsRotationRevocation()
    {
        await database.ResetAsync(Token); await using var api = new IdentityApiFactory(database); using var client = api.Client();
        var first = await Create(api, "access-first@example.test"); var second = await Create(api, "access-second@example.test");
        using var firstLogin = await MfaTestClient.Write(client, "login", new LoginRequest("access-first@example.test", Password), Token);
        Assert.Equal(HttpStatusCode.OK, firstLogin.StatusCode); var recorded = api.Clock.GetUtcNow();
        var oldCookie = firstLogin.Headers.GetValues("Set-Cookie").Single(x => x.StartsWith("__Host-acropolis-session=", StringComparison.Ordinal)).Split(';')[0];
        using var copied = api.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), HandleCookies = false, AllowAutoRedirect = false });
        copied.DefaultRequestHeaders.Add("Cookie", oldCookie);
        api.Clock.Advance(TimeSpan.FromMinutes(1));
        using var secondLogin = await MfaTestClient.Write(client, "login", new LoginRequest("access-second@example.test", Password), Token);
        Assert.Equal(HttpStatusCode.OK, secondLogin.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await copied.GetAsync("/api/v1/identity/me", Token)).StatusCode);
        await using var final = database.Context();
        Assert.Equal(PgUtc(recorded), (await final.AccountAccess.SingleAsync(x => x.UserId == first, Token)).LastSignInUtc);
        Assert.Equal(PgUtc(api.Clock.GetUtcNow()), (await final.AccountAccess.SingleAsync(x => x.UserId == second, Token)).LastSignInUtc);
        Assert.Equal(second, (await final.Sessions.SingleAsync(Token)).UserId);
    }

    [Fact]
    public async Task AdminLookupDistinguishesUnknownUserFromUnknownDateWithoutLoadingOrMutatingUsers()
    {
        await database.ResetAsync(Token); await using var context = database.Context();
        var user = User("access-query"); context.Users.Add(user); await context.SaveChangesAsync(Token); context.ChangeTracker.Clear();
        var service = new AccountAccessService(context);
        Assert.Null(await service.GetAsync(Guid.NewGuid(), Token));
        var view = await service.GetAsync(user.Id, Token); Assert.NotNull(view); Assert.Null(view.LastSignInUtc);
        Assert.Empty(context.ChangeTracker.Entries()); Assert.Empty(await context.AccountAccess.ToArrayAsync(Token));
    }

    private ITicketStore Store(TestClock clock, IInterceptor? interceptor = null) =>
        new PostgresTicketStore(new TicketFactory(database.RuntimeConnection, interceptor), protection, clock);
    private static DateTimeOffset PgUtc(DateTimeOffset value) => new(value.UtcTicks - value.UtcTicks % 10, TimeSpan.Zero);
    private static ChannelUser User(string suffix) => new()
    {
        Id = Guid.NewGuid(),
        DisplayName = "Synthetic account",
        UserName = suffix + "@example.test",
        NormalizedUserName = (suffix + "@example.test").ToUpperInvariant(),
        Email = suffix + "@example.test",
        NormalizedEmail = (suffix + "@example.test").ToUpperInvariant(),
        EmailConfirmed = true,
        ConcurrencyStamp = Guid.NewGuid().ToString("N"),
        SecurityStamp = Guid.NewGuid().ToString("N")
    };
    private static AuthenticationTicket Ticket(ChannelUser user, TestClock clock, string? method = "pwd")
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, user.Id.ToString()), new("auth_version", user.SecurityVersion) };
        if (method is not null) claims.Add(new("amr", method));
        return new(new ClaimsPrincipal(new ClaimsIdentity(claims, "Identity.Application")),
            new AuthenticationProperties { ExpiresUtc = clock.GetUtcNow() + IdentityRules.SessionLifetime }, "Identity.Application");
    }
    private static Task<long> UserXmin(IdentityDbContext context, Guid id) =>
        context.Database.SqlQuery<long>($"SELECT xmin::text::bigint AS \"Value\" FROM identity.\"Users\" WHERE \"Id\"={id}").SingleAsync(Token);
    private async Task<Guid> Create(IdentityApiFactory api, string email, bool administrator = false)
    {
        await using var scope = api.Services.CreateAsyncScope(); var users = scope.ServiceProvider.GetRequiredService<UserManager<ChannelUser>>();
        var user = User(email.Split('@')[0]); user.Email = user.UserName = email;
        Assert.True((await users.CreateAsync(user, Password)).Succeeded);
        if (administrator) { await using var owner = database.Context(true); await new IdentityOperations(owner, api.Clock).BootstrapAsync(email, Token); }
        return user.Id;
    }
    private sealed class TicketFactory(string connection, IInterceptor? interceptor) : IDbContextFactory<IdentityDbContext>
    {
        public IdentityDbContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<IdentityDbContext>(); IdentityRegistration.ConfigureDatabase(options, connection);
            if (interceptor is not null) options.AddInterceptors(interceptor);
            return new IdentityDbContext(options.Options);
        }
    }
    private sealed class BeforeAccess(Action action) : DbCommandInterceptor
    {
        public int Calls { get; private set; }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("INSERT INTO identity.\"AccountAccess\"", StringComparison.Ordinal)) { Calls++; action(); }
            return ValueTask.FromResult(result);
        }
    }
}
