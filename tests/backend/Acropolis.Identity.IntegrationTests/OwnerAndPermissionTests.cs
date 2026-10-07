using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Acropolis.Identity.Application;
using Acropolis.Identity.Infrastructure;
using Acropolis.Migrations;
using Acropolis.TestSupport;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Acropolis.Identity.IntegrationTests;

[Collection("IdentityPostgres")]
public sealed class OwnerAndPermissionTests(IdentityFixture database)
{
    private const string Password = "Acropolis protected owner phrase";
    [Fact]
    public async Task OwnerDesignationIsExplicitConcurrentIdempotentAndCannotUseOldRecoveryCredentials()
    {
        var token = TestContext.Current.CancellationToken;
        await database.ResetAsync(token);
        await using var api = new IdentityApiFactory(database);
        var owner = await Create(api, "owner-bootstrap@example.test", token);
        await using (var context = database.Context(true))
        {
            var operations = new IdentityOperations(context, api.Clock);
            await Assert.ThrowsAsync<InvalidOperationException>(() => operations.BootstrapOwnerAsync(owner.Email!, null, token));
            await Assert.ThrowsAsync<InvalidOperationException>(() => operations.BootstrapOwnerAsync(owner.Email!, "typo@example.test", token));
            await Assert.ThrowsAsync<InvalidOperationException>(() => operations.BootstrapOwnerAsync("missing@example.test", "missing@example.test", token));
        }
        async Task Bootstrap()
        {
            await using var context = database.Context(true);
            await new IdentityOperations(context, api.Clock).BootstrapOwnerAsync(owner.Email!.ToUpperInvariant(), owner.Email, token);
        }
        await Task.WhenAll(Bootstrap(), Bootstrap());
        await using (var context = database.Context())
        {
            var stored = await context.Users.SingleAsync(x => x.Id == owner.Id, token);
            Assert.True(stored.IsOwner && stored.UsersManage && stored.ContentManage && stored.SubscriptionsManage);
            Assert.False(stored.TwoFactorEnabled);
            Assert.Equal(owner.PasswordHash, stored.PasswordHash);
            Assert.Equal(1, await context.Audit.CountAsync(x => x.Action == "owner.bootstrap", token));
        }
        await using (var context = database.Context(true))
        {
            var operations = new IdentityOperations(context, api.Clock);
            await Assert.ThrowsAsync<InvalidOperationException>(() => operations.SetContentManagerAsync(owner.Email!, false, token));
            await operations.InvalidateRecoveryAsync(token);
            context.ChangeTracker.Clear();
            var quarantined = await context.Users.SingleAsync(x => x.Id == owner.Id, token);
            Assert.True(quarantined.RevalidationRequired);
            Assert.False(quarantined.IsOwner || quarantined.UsersManage || quarantined.ContentManage || quarantined.SubscriptionsManage || quarantined.TwoFactorEnabled);
            context.ChangeTracker.Clear();
            await Assert.ThrowsAsync<InvalidOperationException>(() => operations.BootstrapOwnerAsync(owner.Email!, owner.Email, token));
            await operations.RevalidateAsync(owner.Email!, false, token);
            context.ChangeTracker.Clear();
            await Assert.ThrowsAsync<InvalidOperationException>(() => operations.RecoverOwnerAsync(owner.Email!, owner.Email, false, token));
            await Assert.ThrowsAsync<InvalidOperationException>(() => operations.RecoverOwnerAsync(owner.Email!, owner.Email, true, token));
        }
        using (var scope = api.Services.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<IIdentityService>();
            await service.RequestEmailAsync(owner.Email!, "confirm", token);
        }
        await api.DispatchAsync();
        var confirm = api.Mailer.Messages.Last(x => x.Purpose == "confirm");
        using (var scope = api.Services.CreateScope()) Assert.True((await scope.ServiceProvider.GetRequiredService<IIdentityService>().ConfirmEmailAsync(new(owner.Id, confirm.Token), token)).Succeeded);
        await using (var context = database.Context(true))
            await Assert.ThrowsAsync<InvalidOperationException>(() => new IdentityOperations(context, api.Clock).RecoverOwnerAsync(owner.Email!, owner.Email, true, token));
        using (var scope = api.Services.CreateScope()) await scope.ServiceProvider.GetRequiredService<IIdentityService>().RequestEmailAsync(owner.Email!, "reset", token);
        await api.DispatchAsync();
        var reset = api.Mailer.Messages.Last(x => x.Purpose == "reset");
        using (var scope = api.Services.CreateScope()) Assert.True((await scope.ServiceProvider.GetRequiredService<IIdentityService>().ResetPasswordAsync(new(owner.Id, reset.Token, Password + " renewed"), token)).Succeeded);
        await using (var context = database.Context(true))
        {
            var operations = new IdentityOperations(context, api.Clock);
            await Assert.ThrowsAsync<InvalidOperationException>(() => operations.BootstrapOwnerAsync(owner.Email!, owner.Email, token));
            await operations.RecoverOwnerAsync(owner.Email!, owner.Email, true, token);
            await operations.RecoverOwnerAsync(owner.Email!, owner.Email, true, token);
            Assert.Equal(1, await context.Audit.CountAsync(x => x.Action == "owner.recovered", token));
        }
    }
    [Fact]
    public async Task OwnerOnlyPermissionHttpMutationRequiresCsrfRevokesSessionsAndProtectsAuthority()
    {
        var token = TestContext.Current.CancellationToken;
        await database.ResetAsync(token);
        await using var api = new IdentityApiFactory(database);
        var owner = await Create(api, "owner-http@example.test", token);
        var delegateUser = await Create(api, "delegate-http@example.test", token, usersManage: true);
        var target = await Create(api, "target-http@example.test", token);
        await using (var context = database.Context(true)) await new IdentityOperations(context, api.Clock).BootstrapOwnerAsync(owner.Email!, owner.Email, token);
        using var ownerClient = api.Client(); using var delegateClient = api.Client(); using var targetClient = api.Client(); using var anonymous = api.Client();
        Assert.Equal(HttpStatusCode.OK, (await MfaTestClient.Login(ownerClient, owner.Email!, Password, token)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await MfaTestClient.Login(delegateClient, delegateUser.Email!, Password, token)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await MfaTestClient.Login(targetClient, target.Email!, Password, token)).StatusCode);
        var current = (await ownerClient.GetFromJsonAsync<UserView>($"/api/v1/admin/users/{target.Id}", token))!;
        Assert.False(current.IsOwner);
        Assert.Equal(HttpStatusCode.Forbidden, (await Write(delegateClient, HttpMethod.Put, $"/api/v1/admin/users/{target.Id}/permissions", new AdminPermissionsRequest(current.Version, [IdentityRules.ManageSubscriptions]), token)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ownerClient.PutAsJsonAsync($"/api/v1/admin/users/{target.Id}/permissions", new AdminPermissionsRequest(current.Version, []), token)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Write(ownerClient, HttpMethod.Put, $"/api/v1/admin/users/{target.Id}/permissions", new { version = current.Version, permissions = new[] { IdentityRules.ManageSubscriptions }, isOwner = true }, token)).StatusCode);
        using var changed = await Write(ownerClient, HttpMethod.Put, $"/api/v1/admin/users/{target.Id}/permissions", new AdminPermissionsRequest(current.Version, [IdentityRules.ManageSubscriptions]), token);
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        var view = (await changed.Content.ReadFromJsonAsync<UserView>(token))!;
        Assert.Equal([IdentityRules.ManageSubscriptions], view.Permissions);
        Assert.Equal(HttpStatusCode.Unauthorized, (await targetClient.GetAsync("/api/v1/identity/me", token)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Write(ownerClient, HttpMethod.Put, $"/api/v1/admin/users/{target.Id}/permissions", new AdminPermissionsRequest(current.Version, []), token)).StatusCode);
        var ownerView = (await ownerClient.GetFromJsonAsync<UserView>("/api/v1/identity/me", token))!;
        Assert.True(ownerView.IsOwner);
        Assert.Equal(HttpStatusCode.Conflict, (await Write(ownerClient, HttpMethod.Put, $"/api/v1/admin/users/{owner.Id}/permissions", new AdminPermissionsRequest(ownerView.Version, [IdentityRules.ManageUsers]), token)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Write(delegateClient, HttpMethod.Patch, $"/api/v1/admin/users/{owner.Id}", new AdminUserRequest(ownerView.Version, Status: "disabled"), token)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/admin/users/audit", token)).StatusCode);
        var audit = (await ownerClient.GetFromJsonAsync<UserAuditPage>($"/api/v1/admin/users/audit?action=permissions.updated&userId={target.Id}&pageSize=1", token))!;
        Assert.Equal(1, audit.Total); Assert.Single(audit.Items); Assert.Equal(["permissions"], audit.Items[0].Changes.Fields);
        Assert.Equal(HttpStatusCode.BadRequest, (await ownerClient.GetAsync("/api/v1/admin/users/audit?action=unknown", token)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ownerClient.GetAsync("/api/v1/admin/users/audit?fromUtc=2027-01-01T00%3A00%3A00Z&toUtc=2026-01-01T00%3A00%3A00Z", token)).StatusCode);
    }
    [Fact]
    public async Task PendingAdministratorCanBeDisabledAndUnchangedUpdatesPreserveSessionVersionAndAudit()
    {
        var token = TestContext.Current.CancellationToken;
        await database.ResetAsync(token);
        await using var api = new IdentityApiFactory(database);
        var administrator = await Create(api, "last-active@example.test", token, usersManage: true);
        var pending = await Create(api, "pending-admin@example.test", token, usersManage: true, confirmed: false);
        using var client = api.Client(); Assert.Equal(HttpStatusCode.OK, (await MfaTestClient.Login(client, administrator.Email!, Password, token)).StatusCode);
        var before = (await client.GetFromJsonAsync<UserView>("/api/v1/identity/me", token))!;
        using var noOp = await Write(client, HttpMethod.Patch, $"/api/v1/admin/users/{administrator.Id}", new AdminUserRequest(before.Version, before.DisplayName, "active", before.Levels), token);
        Assert.Equal(HttpStatusCode.OK, noOp.StatusCode);
        Assert.Equal(before.Version, (await noOp.Content.ReadFromJsonAsync<UserView>(token))!.Version);
        using var profile = await Write(client, HttpMethod.Patch, "/api/v1/identity/me", new ProfileRequest("  " + before.DisplayName + "  "), token);
        Assert.Equal(HttpStatusCode.OK, profile.StatusCode); Assert.Equal(before.Version, (await profile.Content.ReadFromJsonAsync<UserView>(token))!.Version);
        await using var read = database.Context(); Assert.Empty(await read.Audit.Where(x => x.Action == "users.updated").ToArrayAsync(token));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/identity/me", token)).StatusCode);
        using var disabledPending = await Write(client, HttpMethod.Patch, $"/api/v1/admin/users/{pending.Id}", new AdminUserRequest(pending.ConcurrencyStamp!, Status: "disabled"), token);
        Assert.Equal(HttpStatusCode.OK, disabledPending.StatusCode);
        var audit = (await client.GetFromJsonAsync<UserAuditPage>($"/api/v1/admin/users/audit?action=users.updated&userId={pending.Id}&fromUtc=2000-01-01T00%3A00%3A00Z&toUtc=2099-01-01T00%3A00%3A00Z", token))!;
        Assert.Single(audit.Items); Assert.Equal(["status"], audit.Items[0].Changes.Fields);
        Assert.Equal(HttpStatusCode.Conflict, (await Write(client, HttpMethod.Patch, $"/api/v1/admin/users/{administrator.Id}", new AdminUserRequest(before.Version, Status: "disabled"), token)).StatusCode);
    }
    [Fact]
    public async Task PermissionVersionsAndNoOpAreAtomicAndInactiveAccountsCannotReceiveAuthority()
    {
        var token = TestContext.Current.CancellationToken;
        await database.ResetAsync(token);
        await using var api = new IdentityApiFactory(database);
        var owner = await Create(api, "owner-grants@example.test", token);
        var pending = await Create(api, "pending-grants@example.test", token, confirmed: false);
        var target = await Create(api, "target-grants@example.test", token);
        await using (var context = database.Context(true)) await new IdentityOperations(context, api.Clock).BootstrapOwnerAsync(owner.Email!, owner.Email, token);
        async Task<IdentityResult<UserView>> Change(Guid actor, Guid id, string version, string[] permissions)
        {
            using var scope = api.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<IIdentityService>().UpdatePermissionsAsync(actor, id, new(version, permissions), token);
        }
        Assert.Equal("validation_error", (await Change(owner.Id, target.Id, "", [])).Error);
        Assert.Equal("validation_error", (await Change(owner.Id, target.Id, target.ConcurrencyStamp!, ["Unknown"])).Error);
        Assert.Equal("forbidden", (await Change(target.Id, owner.Id, owner.ConcurrencyStamp!, [])).Error);
        Assert.Equal("not_found", (await Change(owner.Id, Guid.NewGuid(), "version", [])).Error);
        Assert.Equal("account_not_active", (await Change(owner.Id, pending.Id, pending.ConcurrencyStamp!, [IdentityRules.ManageSubscriptions])).Error);
        var changes = await Task.WhenAll(Change(owner.Id, target.Id, target.ConcurrencyStamp!, [IdentityRules.ManageSubscriptions]), Change(owner.Id, target.Id, target.ConcurrencyStamp!, [IdentityRules.ManageContent]));
        Assert.Single(changes, x => x.Succeeded); Assert.Equal("concurrency_conflict", changes.Single(x => !x.Succeeded).Error);
        var result = changes.Single(x => x.Succeeded).Value!;
        await using (var context = database.Context())
        {
            var stored = await context.Users.SingleAsync(x => x.Id == target.Id, token);
            context.Sessions.Add(new StoredSession { Id = new string('f', 64), UserId = target.Id, SecurityVersion = stored.SecurityVersion, Ticket = [1], CreatedUtc = api.Clock.GetUtcNow(), ExpiresUtc = api.Clock.GetUtcNow().AddHours(8) });
            await context.SaveChangesAsync(token);
        }
        Assert.Equal(result.Version, (await Change(owner.Id, target.Id, result.Version, result.Permissions)).Value!.Version);
        await using (var context = database.Context())
        {
            Assert.Equal(1, await context.Audit.CountAsync(x => x.Action == "permissions.updated", token));
            Assert.Equal(1, await context.Sessions.CountAsync(token));
        }
        Assert.Empty((await Change(owner.Id, target.Id, result.Version, [])).Value!.Permissions);
        await using (var context = database.Context()) Assert.Empty(await context.Sessions.ToArrayAsync(token));
    }

    private static async Task<ChannelUser> Create(IdentityApiFactory api, string email, CancellationToken token, bool usersManage = false, bool confirmed = true)
    {
        using var scope = api.Services.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<UserManager<ChannelUser>>();
        var user = new ChannelUser { Id = Guid.NewGuid(), UserName = email, Email = email, DisplayName = "QA Account", EmailConfirmed = confirmed, UsersManage = usersManage, LockoutEnabled = true };
        Assert.True((await manager.CreateAsync(user, Password)).Succeeded);
        token.ThrowIfCancellationRequested(); return user;
    }
    private static async Task<HttpResponseMessage> Write<T>(HttpClient client, HttpMethod method, string url, T body, CancellationToken token)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/v1/identity/csrf", token);
        using var request = new HttpRequestMessage(method, url) { Content = JsonContent.Create(body) };
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());
        return await client.SendAsync(request, token);
    }
}
