using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Acropolis.Identity.Application;
using Acropolis.Identity.Infrastructure;
using Acropolis.Identity.IntegrationTests;
using Acropolis.TestSupport;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Acropolis.Subscriptions.IntegrationTests;

[Collection("IdentityPostgres")]
public sealed class AccountLookupTests(IdentityFixture database)
{
    private const string Password = "Acropolis account lookup QA phrase";
    private const string Route = "/api/v1/admin/subscriptions/accounts/lookup";

    [Fact]
    public async Task LookupRequiresSubscriptionAuthorityAndCompletedMfaAndReturnsOnlyMinimalAccounts()
    {
        var token = TestContext.Current.CancellationToken;
        await database.ResetAsync(token);
        await using var api = new IdentityApiFactory(database);
        var subscriber = await Create(api, "lookup-subscriber@example.test", token);
        var usersManager = await Create(api, "lookup-users-manager@example.test", token, users: true);
        var admin = await Create(api, "lookup-subscription-manager@example.test", token, subscriptions: true);
        var pending = await Create(api, "lookup-pending@example.test", token, confirmed: false);
        var disabled = await Create(api, "lookup-disabled@example.test", token, disabled: true);
        var quarantined = await Create(api, "lookup-quarantined@example.test", token, revalidation: true);
        var unknown = Guid.NewGuid();
        var url = Route + "?userIds=" + string.Join(",", new[] { subscriber.Id, pending.Id, disabled.Id, quarantined.Id, unknown });
        using var anonymous = api.Client();
        using var ordinary = api.Client();
        using var users = api.Client();
        using var manager = api.Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(url, token)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await MfaTestClient.Login(ordinary, subscriber.Email!, Password, token)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ordinary.GetAsync(url, token)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await MfaTestClient.Login(users, usersManager.Email!, Password, token)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await users.GetAsync(url, token)).StatusCode);

        using var firstFactor = await MfaTestClient.Write(manager, "login", new LoginRequest(admin.Email!, Password), token);
        Assert.Equal(HttpStatusCode.Accepted, firstFactor.StatusCode);
        Assert.True((await firstFactor.Content.ReadFromJsonAsync<MfaChallengeView>(token))!.EnrollmentRequired);
        Assert.Equal(HttpStatusCode.Unauthorized, (await manager.GetAsync(url, token)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await MfaTestClient.Login(manager, admin.Email!, Password, token)).StatusCode);
        using var response = await manager.GetAsync(url, token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var rows = await response.Content.ReadFromJsonAsync<JsonElement>(token);
        Assert.Equal(JsonValueKind.Array, rows.ValueKind);
        Assert.Equal(4, rows.GetArrayLength());
        foreach (var row in rows.EnumerateArray())
        {
            Assert.Equal(JsonValueKind.Object, row.ValueKind);
            Assert.Equal(JsonValueKind.String, row.GetProperty("id").ValueKind);
            Assert.Equal(JsonValueKind.String, row.GetProperty("displayName").ValueKind);
            Assert.Equal(JsonValueKind.String, row.GetProperty("email").ValueKind);
            Assert.Equal(JsonValueKind.String, row.GetProperty("status").ValueKind);
            Assert.True(row.GetProperty("emailConfirmed").ValueKind is JsonValueKind.True or JsonValueKind.False);
            Assert.Equal(new[] { "displayName", "email", "emailConfirmed", "id", "status" },
                row.EnumerateObject().Select(x => x.Name).Order(StringComparer.Ordinal).ToArray());
        }
        var accounts = rows.EnumerateArray().ToDictionary(x => x.GetProperty("id").GetGuid());
        Assert.False(accounts.ContainsKey(unknown));
        Assert.Equal(subscriber.DisplayName, accounts[subscriber.Id].GetProperty("displayName").GetString());
        Assert.Equal(subscriber.Email, accounts[subscriber.Id].GetProperty("email").GetString());
        Assert.Equal("active", accounts[subscriber.Id].GetProperty("status").GetString());
        Assert.Equal("pending", accounts[pending.Id].GetProperty("status").GetString());
        Assert.False(accounts[pending.Id].GetProperty("emailConfirmed").GetBoolean());
        Assert.Equal("disabled", accounts[disabled.Id].GetProperty("status").GetString());
        Assert.Equal("disabled", accounts[quarantined.Id].GetProperty("status").GetString());

        using var missing = await manager.GetAsync(Route + "?userIds=" + unknown, token);
        Assert.Equal(HttpStatusCode.OK, missing.StatusCode);
        Assert.Empty((await missing.Content.ReadFromJsonAsync<IdentityAccountSummary[]>(token))!);
        var twenty = Enumerable.Range(0, 19).Select(_ => Guid.NewGuid()).Prepend(subscriber.Id).ToArray();
        using var boundary = await manager.GetAsync(Route + "?userIds=" + string.Join(",", twenty), token);
        Assert.Equal(HttpStatusCode.OK, boundary.StatusCode);
        Assert.Equal(subscriber.Id, Assert.Single((await boundary.Content.ReadFromJsonAsync<IdentityAccountSummary[]>(token))!).Id);
        var invalidQueries = new[]
        {
            "", "?userIds=", "?userIds=not-a-guid", "?userIds=" + Guid.Empty,
            "?userIds=" + subscriber.Id + ",", "?userIds=" + subscriber.Id + "," + subscriber.Id,
            "?userIds=" + string.Join(",", twenty.Append(Guid.NewGuid())), "?userIds=" + new string('a', 740)
        };
        foreach (var query in invalidQueries)
        {
            using var invalid = await manager.GetAsync(Route + query, token);
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            Assert.Equal("validation_error", (await invalid.Content.ReadFromJsonAsync<JsonElement>(token)).GetProperty("code").GetString());
            Assert.True(invalid.Headers.CacheControl?.NoStore);
        }
    }

    [Fact]
    public async Task LookupUsesOneBoundedSqlProjectionWithoutLoadingAuthorityCredentialsOrLevels()
    {
        var token = TestContext.Current.CancellationToken;
        await database.ResetAsync(token);
        await using var api = new IdentityApiFactory(database);
        var active = await Create(api, "lookup-projection-active@example.test", token, users: true, subscriptions: true);
        var pending = await Create(api, "lookup-projection-pending@example.test", token, confirmed: false);
        var disabled = await Create(api, "lookup-projection-disabled@example.test", token, disabled: true);
        var revalidation = await Create(api, "lookup-projection-revalidation@example.test", token, revalidation: true);
        var observer = new LookupCommands();
        var options = new DbContextOptionsBuilder<IdentityDbContext>();
        IdentityRegistration.ConfigureDatabase(options, database.RuntimeConnection);
        options.AddInterceptors(observer);
        await using var context = new IdentityDbContext(options.Options);
        using var scope = api.Services.CreateScope();
        var service = new IdentityService(context, scope.ServiceProvider.GetRequiredService<UserManager<ChannelUser>>(),
            scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>(), api.Clock);
        var rows = await service.LookupAccountsAsync(new[] { active.Id, pending.Id, disabled.Id, revalidation.Id, Guid.NewGuid() }, token);
        Assert.Equal(4, rows.Length);
        Assert.Equal("active", rows.Single(x => x.Id == active.Id).Status);
        Assert.Equal("pending", rows.Single(x => x.Id == pending.Id).Status);
        Assert.Equal("disabled", rows.Single(x => x.Id == disabled.Id).Status);
        Assert.Equal("disabled", rows.Single(x => x.Id == revalidation.Id).Status);
        Assert.Empty(context.ChangeTracker.Entries());
        var sql = Assert.Single(observer.Commands);
        Assert.Contains("CASE", sql, StringComparison.Ordinal);
        Assert.Contains("WHERE", sql, StringComparison.Ordinal);
        foreach (var field in new[] { "PasswordHash", "SecurityStamp", "SecurityVersion", "ConcurrencyStamp", "UsersManage", "ContentManage", "SubscriptionsManage", "IsOwner", "TwoFactorEnabled", "UserLevels" })
            Assert.DoesNotContain(field, sql, StringComparison.Ordinal);
        foreach (var invalid in new[]
        {
            Array.Empty<Guid>(), new[] { Guid.Empty }, new[] { active.Id, active.Id },
            Enumerable.Range(0, 21).Select(_ => Guid.NewGuid()).ToArray()
        })
            await Assert.ThrowsAsync<ArgumentException>(() => service.LookupAccountsAsync(invalid, token));
        Assert.Single(observer.Commands);
    }

    private static async Task<ChannelUser> Create(IdentityApiFactory api, string email, CancellationToken token,
        bool users = false, bool subscriptions = false, bool confirmed = true, bool disabled = false, bool revalidation = false)
    {
        using var scope = api.Services.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<UserManager<ChannelUser>>();
        var user = new ChannelUser
        {
            Id = Guid.NewGuid(),
            UserName = email,
            Email = email,
            DisplayName = "QA Account Lookup",
            EmailConfirmed = confirmed,
            IsDisabled = disabled,
            RevalidationRequired = revalidation,
            UsersManage = users,
            SubscriptionsManage = subscriptions,
            LockoutEnabled = true
        };
        Assert.True((await manager.CreateAsync(user, Password)).Succeeded);
        token.ThrowIfCancellationRequested();
        return user;
    }

    private sealed class LookupCommands : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }
}
