using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using Acropolis.Identity.Application;
using Acropolis.Identity.Infrastructure;
using Acropolis.Migrations;
using Acropolis.TestSupport;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Acropolis.Identity.IntegrationTests;

[Collection("IdentityPostgres")]
public sealed class EditorialPermissionConcurrencyTests(IdentityFixture database)
{
    private const string Password = "Acropolis editorial concurrency QA";
    private const string NewPassword = "Acropolis reset concurrency new phrase";

    [Fact]
    public async Task EditorialGrantWaitsForPasswordResetWithoutDeadlockAndBothChangesRemainAtomic()
    {
        var token = TestContext.Current.CancellationToken;
        await database.ResetAsync(token);
        await using var api = new IdentityApiFactory(database);
        ChannelUser user;
        using (var setup = api.Services.CreateScope())
        {
            var manager = setup.ServiceProvider.GetRequiredService<UserManager<ChannelUser>>();
            user = new ChannelUser
            {
                Id = Guid.NewGuid(),
                UserName = "editorial-lock-order@example.test",
                Email = "editorial-lock-order@example.test",
                EmailConfirmed = true,
                DisplayName = "QA Editorial Lock Order",
                LockoutEnabled = true
            };
            Assert.True((await manager.CreateAsync(user, Password)).Succeeded);
            await setup.ServiceProvider.GetRequiredService<IIdentityService>().RequestEmailAsync(user.Email!, "reset", token);
        }
        await api.DispatchAsync();
        var resetMail = Assert.Single(api.Mailer.Messages, x => x.Purpose == "reset");
        using var client = api.Client();
        Assert.Equal(HttpStatusCode.OK, (await MfaTestClient.Login(client, user.Email!, Password, token)).StatusCode);
        await using (var before = database.Context())
            Assert.Single(await before.Sessions.Where(x => x.UserId == user.Id).ToArrayAsync(token));

        var barrier = new SessionDeleteBarrier();
        var options = new DbContextOptionsBuilder<IdentityDbContext>();
        IdentityRegistration.ConfigureDatabase(options, database.RuntimeConnection);
        options.AddInterceptors(barrier);
        await using var resetContext = new IdentityDbContext(options.Options);
        using var resetScope = api.Services.CreateScope();
        var protection = resetScope.ServiceProvider.GetRequiredService<IDataProtectionProvider>();
        var store = new MfaUserStore(resetContext, protection, new MfaVerificationKey());
        using var resetManager = ActivatorUtilities.CreateInstance<UserManager<ChannelUser>>(resetScope.ServiceProvider, store);
        var resetService = new IdentityService(resetContext, resetManager, protection, api.Clock);
        await using var cliContext = database.Context(true);
        await cliContext.Database.OpenConnectionAsync(token);
        var cliConnection = (NpgsqlConnection)cliContext.Database.GetDbConnection();
        await using var process = new NpgsqlCommand("SELECT pg_backend_pid()", cliConnection);
        var processId = Convert.ToInt32(await process.ExecuteScalarAsync(token));
        using var operationDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        operationDeadline.CancelAfter(TimeSpan.FromSeconds(15));
        var operationToken = operationDeadline.Token;
        var reset = resetService.ResetPasswordAsync(new ResetPasswordRequest(user.Id, resetMail.Token, NewPassword), operationToken);
        Task grant = Task.CompletedTask;
        var contentionObserved = false;
        try
        {
            await barrier.Deleted.Task.WaitAsync(TimeSpan.FromSeconds(5), operationToken);
            grant = new IdentityOperations(cliContext, api.Clock).SetContentManagerAsync(user.Email!, true, operationToken);
            await WaitForPostgresLockAsync(processId, grant, operationToken);
            contentionObserved = true;
        }
        finally
        {
            barrier.Release.TrySetResult();
            if (!contentionObserved) operationDeadline.Cancel();
            await Task.WhenAll(reset, grant);
        }
        Assert.True((await reset).Succeeded);
        await using (var final = database.Context())
        {
            var stored = await final.Users.AsNoTracking().SingleAsync(x => x.Id == user.Id, token);
            Assert.True(stored.ContentManage);
            Assert.False(stored.UsersManage);
            Assert.False(stored.SubscriptionsManage);
            Assert.False(stored.IsOwner);
            Assert.NotEqual(user.SecurityVersion, stored.SecurityVersion);
            Assert.Empty(await final.Sessions.Where(x => x.UserId == user.Id).ToArrayAsync(token));
            Assert.NotNull((await final.Flows.SingleAsync(x => x.UserId == user.Id && x.Purpose == "reset", token)).ConsumedUtc);
            Assert.Equal(1, await final.Audit.CountAsync(x => x.UserId == user.Id && x.Action == "content.permission.granted", token));
        }
        using (var verify = api.Services.CreateScope())
        {
            var manager = verify.ServiceProvider.GetRequiredService<UserManager<ChannelUser>>();
            var stored = (await manager.FindByIdAsync(user.Id.ToString()))!;
            Assert.True(await manager.CheckPasswordAsync(stored, NewPassword));
            Assert.False(await manager.CheckPasswordAsync(stored, Password));
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/identity/me", token)).StatusCode);
        using var next = api.Client();
        using var login = await MfaTestClient.Write(next, "login", new LoginRequest(user.Email!, NewPassword), token);
        Assert.Equal(HttpStatusCode.Accepted, login.StatusCode);
        Assert.True((await login.Content.ReadFromJsonAsync<MfaChallengeView>(token))!.EnrollmentRequired);
        Assert.Equal(HttpStatusCode.Unauthorized, (await next.GetAsync("/api/v1/identity/me", token)).StatusCode);
    }

    private async Task WaitForPostgresLockAsync(int processId, Task grant, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        await using var observer = new NpgsqlConnection(database.AdminConnection);
        await observer.OpenAsync(deadline.Token);
        await using var command = new NpgsqlCommand("SELECT wait_event_type FROM pg_stat_activity WHERE pid=@pid AND datname=current_database() AND state='active'", observer);
        command.Parameters.AddWithValue("pid", processId);
        while (true)
        {
            if (await command.ExecuteScalarAsync(deadline.Token) is string value && value == "Lock") return;
            if (grant.IsCompleted)
            {
                await grant;
                Assert.Fail("The editorial command must contend with the reset transaction before the barrier is released.");
            }
        }
    }

    private sealed class SessionDeleteBarrier : DbCommandInterceptor
    {
        public TaskCompletionSource Deleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            int result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.TrimStart().StartsWith("DELETE", StringComparison.Ordinal) &&
                command.CommandText.Contains("\"Sessions\"", StringComparison.Ordinal))
            {
                Assert.Equal(1, result);
                Deleted.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }
}
