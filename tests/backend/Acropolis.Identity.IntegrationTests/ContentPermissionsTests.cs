using Acropolis.Identity.Application;
using Acropolis.Identity.Infrastructure;
using Acropolis.Migrations;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Acropolis.Identity.IntegrationTests;

[Collection("IdentityPostgres")]
public sealed class ContentPermissionsTests(IdentityFixture database)
{
    [Fact]
    public async Task EditorialPermissionIsExplicitIndependentAuditedAndRevokesSessions()
    {
        var token = TestContext.Current.CancellationToken;
        await database.ResetAsync(token);
        await using var api = new IdentityApiFactory(database);
        using var scope = api.Services.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<UserManager<ChannelUser>>();
        var user = new ChannelUser { Id = Guid.NewGuid(), UserName = "editor@example.test", Email = "editor@example.test", DisplayName = "Editor", EmailConfirmed = true };
        Assert.True((await manager.CreateAsync(user, "Acropolis editorial phrase")).Succeeded);
        await using var context = database.Context(true);
        var operations = new IdentityOperations(context, api.Clock);
        await Assert.ThrowsAsync<InvalidOperationException>(() => operations.SetContentManagerAsync("missing@example.test", true, token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => operations.SetContentManagerAsync("invalid", true, token));
        await operations.SetContentManagerAsync(user.Email, true, token);
        await operations.SetContentManagerAsync(user.Email, true, token);
        await using (var read = database.Context())
        {
            var stored = await read.Users.SingleAsync(x => x.Id == user.Id, token);
            Assert.True(stored.ContentManage);
            Assert.False(stored.UsersManage);
            Assert.Equal(1, await read.Audit.CountAsync(x => x.Action == "content.permission.granted", token));
            read.Sessions.Add(new StoredSession { Id = new string('a', 64), UserId = user.Id, SecurityVersion = stored.SecurityVersion, Ticket = [1], CreatedUtc = api.Clock.GetUtcNow(), ExpiresUtc = api.Clock.GetUtcNow().AddHours(8) });
            await read.SaveChangesAsync(token);
        }
        using var viewScope = api.Services.CreateScope();
        var service = viewScope.ServiceProvider.GetRequiredService<IIdentityService>();
        Assert.Equal([IdentityRules.ManageContent], (await service.GetUserAsync(user.Id, token))!.Permissions);
        await operations.SetContentManagerAsync(user.Email, false, token);
        await operations.SetContentManagerAsync(user.Email, false, token);
        await using var final = database.Context();
        Assert.False((await final.Users.SingleAsync(x => x.Id == user.Id, token)).ContentManage);
        Assert.Empty(await final.Sessions.ToArrayAsync(token));
        Assert.Equal(1, await final.Audit.CountAsync(x => x.Action == "content.permission.revoked", token));
        await operations.SetContentManagerAsync(user.Email, true, token);
        await operations.InvalidateRecoveryAsync(token);
        context.ChangeTracker.Clear();
        await Assert.ThrowsAsync<InvalidOperationException>(() => operations.SetContentManagerAsync(user.Email, true, token));
        context.ChangeTracker.Clear();
        await operations.RevalidateAsync(user.Email, true, token);
        await using var recovered = database.Context();
        var recoveredUser = await recovered.Users.SingleAsync(x => x.Id == user.Id, token);
        Assert.False(recoveredUser.ContentManage);
        Assert.True(recoveredUser.UsersManage);
    }

    [Fact]
    public async Task PendingAndDisabledAccountsCannotReceiveEditorialPermission()
    {
        var token = TestContext.Current.CancellationToken;
        await database.ResetAsync(token);
        await using var context = database.Context(true);
        context.Users.Add(new ChannelUser { Id = Guid.NewGuid(), Email = "pending@example.test", UserName = "pending@example.test", NormalizedEmail = "PENDING@EXAMPLE.TEST", NormalizedUserName = "PENDING@EXAMPLE.TEST", DisplayName = "Pending" });
        context.Users.Add(new ChannelUser { Id = Guid.NewGuid(), Email = "disabled@example.test", UserName = "disabled@example.test", NormalizedEmail = "DISABLED@EXAMPLE.TEST", NormalizedUserName = "DISABLED@EXAMPLE.TEST", DisplayName = "Disabled", EmailConfirmed = true, IsDisabled = true });
        await context.SaveChangesAsync(token);
        var operations = new IdentityOperations(context, TimeProvider.System);
        foreach (var email in new[] { "pending@example.test", "disabled@example.test" })
            await Assert.ThrowsAsync<InvalidOperationException>(() => operations.SetContentManagerAsync(email, true, token));
    }
}
