using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Acropolis.Identity.Application;
using Acropolis.Identity.Infrastructure;
using Acropolis.Migrations;
using Acropolis.TestSupport;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
namespace Acropolis.Identity.IntegrationTests;

[Collection("IdentityPostgres")]
public sealed class MfaTests(IdentityFixture database)
{
    private const string Password = "MFA isolated QA secure phrase 2026";
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static Task<HttpResponseMessage> Write<T>(HttpClient client, string path, T body) => MfaTestClient.Write(client, path, body, Token);
    private async Task<Guid> User(IdentityApiFactory api, string email, bool administrator = false)
    {
        await using var scope = api.Services.CreateAsyncScope();
        var manager = scope.ServiceProvider.GetRequiredService<UserManager<ChannelUser>>();
        var user = new ChannelUser { Id = Guid.NewGuid(), Email = email, UserName = email, DisplayName = "MFA QA", EmailConfirmed = true, LockoutEnabled = true };
        user.Levels.Add(new UserLevel { UserId = user.Id, Level = "Externo" });
        Assert.True((await manager.CreateAsync(user, Password)).Succeeded);
        if (administrator) { await using var owner = database.Context(true); await new IdentityOperations(owner, api.Clock).BootstrapAsync(email, Token); }
        return user.Id;
    }
    private static async Task<MfaEnrollmentView> Enrollment(HttpClient client, string email, bool administrator)
    {
        using var login = await Write(client, "login", new LoginRequest(email, Password));
        if (administrator)
        {
            Assert.Equal(HttpStatusCode.Accepted, login.StatusCode);
            Assert.False(login.Headers.TryGetValues("Set-Cookie", out _));
            var challenge = (await login.Content.ReadFromJsonAsync<MfaChallengeView>(Token))!;
            Assert.True(challenge.MfaRequired); Assert.True(challenge.EnrollmentRequired);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/identity/me", Token)).StatusCode);
            using var start = await Write(client, "mfa/enrollment", new MfaEnrollmentRequest(ChallengeToken: challenge.ChallengeToken));
            Assert.Equal(HttpStatusCode.OK, start.StatusCode);
            return (await start.Content.ReadFromJsonAsync<MfaEnrollmentView>(Token))!;
        }
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        using var enrollment = await Write(client, "mfa/enrollment", new MfaEnrollmentRequest(CurrentPassword: Password));
        Assert.Equal(HttpStatusCode.OK, enrollment.StatusCode);
        return (await enrollment.Content.ReadFromJsonAsync<MfaEnrollmentView>(Token))!;
    }
    private static async Task<(UserView User, string[] Codes)> Enable(HttpClient client, MfaEnrollmentView enrollment)
    {
        using var enabled = await Write(client, "mfa/enable", new MfaEnableRequest(enrollment.ChallengeToken, MfaTestClient.FreshCode(enrollment.SharedKey)));
        Assert.Equal(HttpStatusCode.OK, enabled.StatusCode);
        var body = await enabled.Content.ReadFromJsonAsync<JsonElement>(Token);
        return (body.GetProperty("user").Deserialize<UserView>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!, body.GetProperty("recoveryCodes").Deserialize<string[]>()!);
    }
    [Fact]
    public async Task AdministrativePasswordProducesOnlyLimitedEnrollmentThenOfficialTotpGrantsMfaSession()
    {
        await database.ResetAsync(Token); await using var api = new IdentityApiFactory(database); using var client = api.Client();
        var id = await User(api, "mfa-admin@example.test", true);
        var enrollment = await Enrollment(client, "mfa-admin@example.test", true);
        var hasAuthenticatorScheme = enrollment.AuthenticatorUri.StartsWith("otpauth://totp/", StringComparison.Ordinal);
        Assert.True(hasAuthenticatorScheme); Assert.True(enrollment.AuthenticatorUri.Contains("digits=6&period=30", StringComparison.Ordinal));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/admin/users", Token)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/identity/mfa/enable", new MfaEnableRequest(enrollment.ChallengeToken, "123456"), Token)).StatusCode);
        var enabled = await Enable(client, enrollment);
        Assert.Equal(id, enabled.User.Id); Assert.Equal(10, enabled.Codes.Length); Assert.Equal(10, enabled.Codes.Distinct().Count());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/admin/users", Token)).StatusCode);
        var status = await client.GetFromJsonAsync<MfaStatusView>("/api/v1/identity/mfa", Token);
        Assert.True(status!.Enabled); Assert.True(status.Required); Assert.Equal(10, status.RecoveryCodesLeft);
        await using var context = database.Context(); var credential = await context.MfaCredentials.SingleAsync(Token);
        Assert.False(credential.ProtectedKey.Contains(enrollment.SharedKey, StringComparison.Ordinal));
        Assert.All(await context.MfaChallenges.ToArrayAsync(Token), challenge => Assert.True(challenge.ProtectedKey is null));
        Assert.Empty(await context.Set<IdentityUserToken<Guid>>().ToArrayAsync(Token));
        var hashes = await context.MfaRecoveryCodes.Select(x => x.Hash).ToArrayAsync(Token);
        Assert.All(hashes, hash => Assert.Equal(64, hash.Length)); Assert.DoesNotContain(hashes, hash => enabled.Codes.Contains(hash));
        using var disabled = await Write(client, "mfa/disable", new MfaReauthenticateRequest(Password, RecoveryCode: enabled.Codes[0]));
        Assert.Equal(HttpStatusCode.Conflict, disabled.StatusCode);
        Assert.Equal("mfa_required_for_admin", (await disabled.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("code").GetString());
    }
    [Fact]
    public async Task EnrollmentRequiresPasswordAndValidProofAndRevokesExistingSessions()
    {
        await database.ResetAsync(Token); await using var api = new IdentityApiFactory(database); using var client = api.Client(); using var other = api.Client();
        var id = await User(api, "mfa-member@example.test");
        Assert.Equal(HttpStatusCode.Unauthorized, (await Write(client, "mfa/enrollment", new MfaEnrollmentRequest(CurrentPassword: Password))).StatusCode);
        await Write(client, "login", new LoginRequest("mfa-member@example.test", Password));
        await Write(other, "login", new LoginRequest("mfa-member@example.test", Password));
        Assert.Equal(HttpStatusCode.BadRequest, (await Write(client, "mfa/enrollment", new MfaEnrollmentRequest())).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Write(client, "mfa/enrollment", new MfaEnrollmentRequest(CurrentPassword: "incorrect password phrase"))).StatusCode);
        var enrollment = await Enrollment(client, "mfa-member@example.test", false);
        Assert.Equal(HttpStatusCode.BadRequest, (await Write(client, "mfa/enable", new MfaEnableRequest(enrollment.ChallengeToken, "no-code"))).StatusCode);
        await using (var read = database.Context()) { Assert.False((await read.Users.SingleAsync(x => x.Id == id, Token)).TwoFactorEnabled); Assert.Equal(2, (await read.MfaCredentials.SingleAsync(Token)).FailedAttempts); }
        await Enable(client, enrollment);
        Assert.Equal(HttpStatusCode.Unauthorized, (await other.GetAsync("/api/v1/identity/me", Token)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Write(client, "mfa/enrollment", new MfaEnrollmentRequest(CurrentPassword: Password))).StatusCode);
        using var overposted = await Write(client, "mfa/challenge", new { challengeToken = enrollment.ChallengeToken, code = "123456", permissions = new[] { "Users.Manage" } });
        Assert.Equal(HttpStatusCode.BadRequest, overposted.StatusCode);
    }
    [Fact]
    public async Task ChallengeAndRecoveryConsumptionAreAtomicAndTotpCannotReplayAcrossChallenges()
    {
        await database.ResetAsync(Token); await using var api = new IdentityApiFactory(database); using var client = api.Client();
        await User(api, "mfa-race@example.test"); var enrollment = await Enrollment(client, "mfa-race@example.test", false); var enabled = await Enable(client, enrollment);
        await Write(client, "logout", new { });
        using var first = api.Client(); using var second = api.Client();
        var login = await Write(first, "login", new LoginRequest("mfa-race@example.test", Password));
        var challenge = (await login.Content.ReadFromJsonAsync<MfaChallengeView>(Token))!; Assert.False(challenge.EnrollmentRequired);
        var code = MfaTestClient.FreshCode(enrollment.SharedKey);
        var results = await Task.WhenAll(Write(first, "mfa/challenge", new MfaVerifyRequest(challenge.ChallengeToken, Code: code)), Write(second, "mfa/challenge", new MfaVerifyRequest(challenge.ChallengeToken, Code: code)));
        Assert.Single(results, x => x.StatusCode == HttpStatusCode.OK); Assert.Single(results, x => x.StatusCode == HttpStatusCode.BadRequest);
        using var another = api.Client(); var next = await Write(another, "login", new LoginRequest("mfa-race@example.test", Password));
        var nextChallenge = (await next.Content.ReadFromJsonAsync<MfaChallengeView>(Token))!;
        Assert.Equal(HttpStatusCode.BadRequest, (await Write(another, "mfa/challenge", new MfaVerifyRequest(nextChallenge.ChallengeToken, Code: code))).StatusCode);
        var recoveryResults = await Task.WhenAll(Write(first, "mfa/challenge", new MfaVerifyRequest(nextChallenge.ChallengeToken, RecoveryCode: enabled.Codes[0])), Write(second, "mfa/challenge", new MfaVerifyRequest(nextChallenge.ChallengeToken, RecoveryCode: enabled.Codes[0])));
        Assert.Single(recoveryResults, x => x.StatusCode == HttpStatusCode.OK); Assert.Single(recoveryResults, x => x.StatusCode == HttpStatusCode.BadRequest);
        await using var read = database.Context(); Assert.Equal(9, await read.MfaRecoveryCodes.CountAsync(Token));
        Assert.Equal(HttpStatusCode.BadRequest, (await Write(client, "mfa/enable", new MfaEnableRequest(enrollment.ChallengeToken, code))).StatusCode);
    }
    [Fact]
    public async Task ExpiryAttemptsAndAccountLockoutCannotBeResetByCreatingAnotherChallenge()
    {
        await database.ResetAsync(Token); await using var api = new IdentityApiFactory(database); using var client = api.Client();
        await User(api, "mfa-lock@example.test", true); var enrollment = await Enrollment(client, "mfa-lock@example.test", true);
        api.Clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(HttpStatusCode.BadRequest, (await Write(client, "mfa/enable", new MfaEnableRequest(enrollment.ChallengeToken, "123456"))).StatusCode);
        enrollment = await Enrollment(client, "mfa-lock@example.test", true);
        for (var attempt = 0; attempt < 5; attempt++) Assert.Equal(HttpStatusCode.BadRequest, (await Write(client, "mfa/enable", new MfaEnableRequest(enrollment.ChallengeToken, "invalid"))).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Write(client, "login", new LoginRequest("mfa-lock@example.test", Password))).StatusCode);
        api.Clock.Advance(TimeSpan.FromMinutes(16)); enrollment = await Enrollment(client, "mfa-lock@example.test", true); await Enable(client, enrollment);
        api.Clock.Advance(TimeSpan.FromHours(8)); Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/identity/me", Token)).StatusCode);
    }
    [Fact]
    public async Task RegenerationAndDisableRequireReauthenticationRotateCodesAndRevokeSessions()
    {
        await database.ResetAsync(Token); await using var api = new IdentityApiFactory(database); using var client = api.Client();
        await User(api, "mfa-settings@example.test"); var enrollment = await Enrollment(client, "mfa-settings@example.test", false); var enabled = await Enable(client, enrollment);
        Assert.Equal(HttpStatusCode.BadRequest, (await Write(client, "mfa/recovery-codes", new MfaReauthenticateRequest("wrong current phrase", RecoveryCode: enabled.Codes[0]))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Write(client, "mfa/recovery-codes", new MfaReauthenticateRequest(Password, "invalid"))).StatusCode);
        using var rotated = await Write(client, "mfa/recovery-codes", new MfaReauthenticateRequest(Password, RecoveryCode: enabled.Codes[0])); Assert.Equal(HttpStatusCode.OK, rotated.StatusCode);
        var codes = (await rotated.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("recoveryCodes").Deserialize<string[]>()!;
        Assert.Equal(10, codes.Length); var containsPreviousRecoveryCode = codes.Any(code => enabled.Codes.Contains(code));
        Assert.False(containsPreviousRecoveryCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/identity/me", Token)).StatusCode);
        using var login = await Write(client, "login", new LoginRequest("mfa-settings@example.test", Password)); var challenge = (await login.Content.ReadFromJsonAsync<MfaChallengeView>(Token))!;
        Assert.Equal(HttpStatusCode.BadRequest, (await Write(client, "mfa/challenge", new MfaVerifyRequest(challenge.ChallengeToken, RecoveryCode: enabled.Codes[1]))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Write(client, "mfa/challenge", new MfaVerifyRequest(challenge.ChallengeToken, RecoveryCode: codes[0]))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Write(client, "mfa/disable", new MfaReauthenticateRequest(Password, RecoveryCode: codes[1]))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/identity/me", Token)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Write(client, "login", new LoginRequest("mfa-settings@example.test", Password))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Write(client, "mfa/disable", new MfaReauthenticateRequest(Password, RecoveryCode: codes[2]))).StatusCode);
        var status = await client.GetFromJsonAsync<MfaStatusView>("/api/v1/identity/mfa", Token); Assert.False(status!.Enabled); Assert.Equal(0, status.RecoveryCodesLeft);
    }
    [Fact]
    public async Task EnrollmentReauthenticationLocksAfterFiveFailuresAndRecoversAfterFifteenMinutes()
    {
        await database.ResetAsync(Token); await using var api = new IdentityApiFactory(database); using var client = api.Client();
        await User(api, "mfa-reauth@example.test"); await Write(client, "login", new LoginRequest("mfa-reauth@example.test", Password));
        for (var attempt = 0; attempt < 5; attempt++)
            Assert.Equal(HttpStatusCode.BadRequest, (await Write(client, "mfa/enrollment", new MfaEnrollmentRequest(CurrentPassword: "wrong current phrase"))).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Write(client, "mfa/enrollment", new MfaEnrollmentRequest(CurrentPassword: Password))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/identity/me", Token)).StatusCode);
        api.Clock.Advance(TimeSpan.FromMinutes(15));
        Assert.Equal(HttpStatusCode.OK, (await Write(client, "mfa/enrollment", new MfaEnrollmentRequest(CurrentPassword: Password))).StatusCode);
    }
    [Fact]
    public async Task PruningRemovesConsumedChallengesAndExpiredProofsPreservingLiveMfaCredentials()
    {
        await database.ResetAsync(Token); await using var api = new IdentityApiFactory(database); using var client = api.Client();
        var id = await User(api, "mfa-prune@example.test"); var enrollment = await Enrollment(client, "mfa-prune@example.test", false); await Enable(client, enrollment);
        await using var owner = database.Context(true); var operations = new IdentityOperations(owner, api.Clock);
        Assert.Equal(1, await operations.PruneAsync(Token)); Assert.Empty(await owner.MfaChallenges.ToArrayAsync(Token));
        Assert.Single(await owner.MfaProofs.ToArrayAsync(Token)); api.Clock.Advance(TimeSpan.FromMinutes(3));
        Assert.Equal(1, await operations.PruneAsync(Token)); Assert.Empty(await owner.MfaProofs.ToArrayAsync(Token));
        Assert.Equal(0, await operations.PruneAsync(Token)); Assert.Equal(10, await owner.MfaRecoveryCodes.CountAsync(Token));
        Assert.Single(await owner.MfaCredentials.ToArrayAsync(Token)); Assert.True((await owner.Users.SingleAsync(x => x.Id == id, Token)).TwoFactorEnabled);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/identity/me", Token)).StatusCode);
    }
    [Fact]
    public async Task OfficialAuthenticatorStoreKeepsKeysEncryptedAndRejectsCorruptedProtectedMaterial()
    {
        await database.ResetAsync(Token); await using var api = new IdentityApiFactory(database);
        var id = await User(api, "mfa-store@example.test");
        await using (var scope = api.Services.CreateAsyncScope())
        {
            var manager = scope.ServiceProvider.GetRequiredService<UserManager<ChannelUser>>(); var user = (await manager.FindByIdAsync(id.ToString()))!;
            Assert.Null(await manager.GetAuthenticatorKeyAsync(user)); Assert.True((await manager.ResetAuthenticatorKeyAsync(user)).Succeeded);
            var first = await manager.GetAuthenticatorKeyAsync(user); Assert.NotNull(first);
            Assert.True((await manager.ResetAuthenticatorKeyAsync(user)).Succeeded); var second = await manager.GetAuthenticatorKeyAsync(user);
            Assert.True(first != second);
            await using var check = database.Context(); var credential = await check.MfaCredentials.SingleAsync(Token);
            Assert.True(credential.ProtectedKey != second); Assert.Empty(await check.Set<IdentityUserToken<Guid>>().ToArrayAsync(Token));
        }
        await using (var owner = database.Context(true))
            await owner.MfaCredentials.Where(x => x.UserId == id).ExecuteUpdateAsync(x => x.SetProperty(c => c.ProtectedKey, "not-protected"), Token);
        await using var fresh = api.Services.CreateAsyncScope(); var users = fresh.ServiceProvider.GetRequiredService<UserManager<ChannelUser>>();
        var restoredUser = (await users.FindByIdAsync(id.ToString()))!;
        await Assert.ThrowsAsync<CryptographicException>(() => users.GetAuthenticatorKeyAsync(restoredUser));
    }
    [Fact]
    public async Task MissingDisabledAndStaleAccountsCannotUseMfaChallengesOrSettings()
    {
        await database.ResetAsync(Token); await using var api = new IdentityApiFactory(database); using var client = api.Client();
        var id = await User(api, "mfa-guards@example.test", true); var enrollment = await Enrollment(client, "mfa-guards@example.test", true);
        await using (var scope = api.Services.CreateAsyncScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<IMfaService>();
            Assert.Equal(401, (await service.StatusAsync(Guid.NewGuid(), Token)).Status);
            Assert.Equal(401, (await service.DisableAsync(Guid.NewGuid(), new MfaReauthenticateRequest(Password, "123456"), Token)).Status);
            Assert.Equal(400, (await service.VerifyAsync(new MfaVerifyRequest("invalid", Code: "123456"), Token)).Status);
            Assert.Equal(400, (await service.StartEnrollmentAsync(null, new MfaEnrollmentRequest(ChallengeToken: new string('a', 43)), Token)).Status);
        }
        await using (var owner = database.Context(true))
            await owner.Users.Where(x => x.Id == id).ExecuteUpdateAsync(x => x.SetProperty(u => u.SecurityVersion, Guid.NewGuid().ToString("N")), Token);
        Assert.Equal(HttpStatusCode.BadRequest, (await Write(client, "mfa/enable", new MfaEnableRequest(enrollment.ChallengeToken, "123456"))).StatusCode);
        using var login = await Write(client, "login", new LoginRequest("mfa-guards@example.test", Password)); var challenge = (await login.Content.ReadFromJsonAsync<MfaChallengeView>(Token))!;
        await using (var owner = database.Context(true))
            await owner.Users.Where(x => x.Id == id).ExecuteUpdateAsync(x => x.SetProperty(u => u.IsDisabled, true), Token);
        Assert.Equal(HttpStatusCode.BadRequest, (await Write(client, "mfa/enrollment", new MfaEnrollmentRequest(ChallengeToken: challenge.ChallengeToken))).StatusCode);
        await using var final = api.Services.CreateAsyncScope(); var mfa = final.ServiceProvider.GetRequiredService<IMfaService>();
        Assert.Equal(401, (await mfa.StartEnrollmentAsync(id, new MfaEnrollmentRequest(CurrentPassword: Password), Token)).Status);
    }
    [Fact]
    public async Task RecoveryInvalidatesRestoredAuthenticatorMaterialAndRequiresExplicitReenrollment()
    {
        await database.ResetAsync(Token); await using var api = new IdentityApiFactory(database); using var client = api.Client();
        var id = await User(api, "mfa-restore@example.test", true); var enrollment = await Enrollment(client, "mfa-restore@example.test", true); await Enable(client, enrollment);
        await using var maintenance = database.Context(true); var operations = new IdentityOperations(maintenance, api.Clock);
        await operations.InvalidateRecoveryAsync(Token);
        Assert.Empty(await maintenance.MfaCredentials.ToArrayAsync(Token)); Assert.Empty(await maintenance.MfaRecoveryCodes.ToArrayAsync(Token));
        Assert.Empty(await maintenance.MfaChallenges.ToArrayAsync(Token)); Assert.Empty(await maintenance.MfaProofs.ToArrayAsync(Token));
        Assert.False((await maintenance.Users.SingleAsync(x => x.Id == id, Token)).TwoFactorEnabled);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/identity/me", Token)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Write(client, "login", new LoginRequest("mfa-restore@example.test", Password))).StatusCode);
        maintenance.ChangeTracker.Clear(); await operations.RevalidateAsync("mfa-restore@example.test", true, Token);
        var restored = await maintenance.Users.SingleAsync(x => x.Id == id, Token); Assert.Null(restored.PasswordHash); Assert.False(restored.TwoFactorEnabled); Assert.False(restored.EmailConfirmed);
        Assert.True(restored.UsersManage); Assert.False(restored.ContentManage);
    }
    [Fact]
    public async Task SigningInAnotherAccountRotatesTheReferenceAndBindsTheNewTicketToThatAccount()
    {
        await database.ResetAsync(Token); await using var api = new IdentityApiFactory(database); using var client = api.Client();
        await User(api, "rotation-first@example.test"); var second = await User(api, "rotation-second@example.test");
        using var firstLogin = await Write(client, "login", new LoginRequest("rotation-first@example.test", Password));
        var oldCookie = firstLogin.Headers.GetValues("Set-Cookie").Single(x => x.StartsWith("__Host-acropolis-session=", StringComparison.Ordinal)).Split(';')[0];
        using var copied = api.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), HandleCookies = false, AllowAutoRedirect = false });
        copied.DefaultRequestHeaders.Add("Cookie", oldCookie);
        Assert.Equal(HttpStatusCode.OK, (await copied.GetAsync("/api/v1/identity/me", Token)).StatusCode);
        using var secondLogin = await Write(client, "login", new LoginRequest("rotation-second@example.test", Password));
        Assert.Equal(HttpStatusCode.OK, secondLogin.StatusCode);
        var newCookie = secondLogin.Headers.GetValues("Set-Cookie").Single(x => x.StartsWith("__Host-acropolis-session=", StringComparison.Ordinal)).Split(';')[0];
        Assert.True(newCookie != oldCookie);
        Assert.Equal(second, (await client.GetFromJsonAsync<UserView>("/api/v1/identity/me", Token))!.Id);
        Assert.Equal(HttpStatusCode.Unauthorized, (await copied.GetAsync("/api/v1/identity/me", Token)).StatusCode);
        await using var read = database.Context(); var session = await read.Sessions.SingleAsync(Token);
        Assert.Equal(second, session.UserId); Assert.Equal((await read.Users.SingleAsync(x => x.Id == second, Token)).SecurityVersion, session.SecurityVersion);
        // AuthenticationProperties serializes UTC expiry with whole-second precision.
        Assert.InRange(session.ExpiresUtc - session.CreatedUtc, IdentityRules.SessionLifetime - TimeSpan.FromSeconds(1), IdentityRules.SessionLifetime);
    }
    [Fact]
    public async Task RevokedAndExpiredReferencesDoNotPreventFreshSignInOrReactivateTheirOldCopies()
    {
        await database.ResetAsync(Token); await using var api = new IdentityApiFactory(database); using var client = api.Client();
        var id = await User(api, "rotation-stale@example.test");
        foreach (var expire in new[] { false, true })
        {
            using var login = await Write(client, "login", new LoginRequest("rotation-stale@example.test", Password));
            var oldCookie = login.Headers.GetValues("Set-Cookie").Single(x => x.StartsWith("__Host-acropolis-session=", StringComparison.Ordinal)).Split(';')[0];
            if (expire) api.Clock.Advance(IdentityRules.SessionLifetime);
            else { await using var owner = database.Context(true); await owner.Sessions.Where(x => x.UserId == id).ExecuteDeleteAsync(Token); }
            using var copied = api.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), HandleCookies = false, AllowAutoRedirect = false });
            copied.DefaultRequestHeaders.Add("Cookie", oldCookie);
            Assert.Equal(HttpStatusCode.Unauthorized, (await copied.GetAsync("/api/v1/identity/me", Token)).StatusCode);
            using var again = await Write(client, "login", new LoginRequest("rotation-stale@example.test", Password));
            Assert.Equal(HttpStatusCode.OK, again.StatusCode);
            Assert.Equal(id, (await client.GetFromJsonAsync<UserView>("/api/v1/identity/me", Token))!.Id);
            Assert.Equal(HttpStatusCode.Unauthorized, (await copied.GetAsync("/api/v1/identity/me", Token)).StatusCode);
        }
    }
    [Fact]
    public async Task AdministrationBootstrapAndEmailFlowsWaitForTheSameUserLockAsMfa()
    {
        await database.ResetAsync(Token); await using var api = new IdentityApiFactory(database); using var client = api.Client();
        var id = await User(api, "locks-admin@example.test");
        await using (var owner = database.Context(true))
            await WaitForLock(id, () => new IdentityOperations(owner, api.Clock).BootstrapAsync("locks-admin@example.test", Token));
        var target = await User(api, "locks-member@example.test");
        await using (var scope = api.Services.CreateAsyncScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<IIdentityService>(); var view = (await service.GetUserAsync(target, Token))!;
            await WaitForLock(target, async () => Assert.True((await service.UpdateUserAsync(id, target, new AdminUserRequest(view.Version, Levels: ["Miembro"]), Token)).Succeeded));
        }
        Assert.Equal(HttpStatusCode.Accepted, (await Write(client, "register", new RegisterRequest("Lock pending", "locks-pending@example.test", Password))).StatusCode);
        await api.DispatchAsync(); var mail = api.Mailer.Messages.Single();
        await using (var scope = api.Services.CreateAsyncScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<IIdentityService>();
            await WaitForLock(mail.UserId, async () => Assert.True((await service.ConfirmEmailAsync(new ConfirmEmailRequest(mail.UserId, mail.Token), Token)).Succeeded));
            await service.RequestEmailAsync("locks-pending@example.test", "reset", Token);
        }
        await api.DispatchAsync(); mail = api.Mailer.Messages.Last();
        await using (var scope = api.Services.CreateAsyncScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<IIdentityService>();
            await WaitForLock(mail.UserId, async () => Assert.True((await service.ResetPasswordAsync(new ResetPasswordRequest(mail.UserId, mail.Token, "New isolated secure password phrase"), Token)).Succeeded));
            await WaitForLock(mail.UserId, async () => Assert.True((await service.ChangePasswordAsync(mail.UserId, new ChangePasswordRequest("New isolated secure password phrase", Password), Token)).Succeeded));
        }
    }
    private async Task WaitForLock(Guid userId, Func<Task> operation)
    {
        await using var connection = new NpgsqlConnection(database.AdminConnection); await connection.OpenAsync(Token);
        await using var transaction = await connection.BeginTransactionAsync(Token);
        var key = BitConverter.ToInt64(SHA256.HashData(userId.ToByteArray()), 0);
        await using (var hold = new NpgsqlCommand("SELECT pg_advisory_xact_lock($1)", connection, transaction))
        { hold.Parameters.AddWithValue(key); await hold.ExecuteNonQueryAsync(Token); }
        var pending = operation(); var waiting = false;
        for (var attempt = 0; attempt < 80 && !pending.IsCompleted; attempt++)
        {
            await using var observe = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM pg_locks WHERE locktype='advisory' AND NOT granted AND database=(SELECT oid FROM pg_database WHERE datname=current_database()))", connection, transaction);
            waiting = (bool)(await observe.ExecuteScalarAsync(Token))!;
            if (waiting) break; await Task.Delay(25, Token);
        }
        // Release even if the assertion fails so the runner never strands a transaction.
        await transaction.CommitAsync(Token); await pending.WaitAsync(TimeSpan.FromSeconds(10), Token);
        Assert.True(waiting, "The operation must acquire the per-user advisory lock before row updates.");
    }

}
