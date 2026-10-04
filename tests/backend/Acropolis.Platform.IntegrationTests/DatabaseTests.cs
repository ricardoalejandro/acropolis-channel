using System.Net;
using System.Net.Http.Json;
using Acropolis.Platform.Application;
using Acropolis.Platform.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Acropolis.Platform.IntegrationTests;

[Collection("Postgres")]
public sealed class DatabaseTests(PostgresFixture database)
{
    [Fact]
    public async Task MigrationIsIdempotentAndRuntimeCannotChangeHistoryOrCreateObjects()
    {
        var token = TestContext.Current.CancellationToken;
        await database.ResetSchemaAsync(token);
        Assert.True(await database.ScalarAsync<bool>(
            "SELECT bool_and(NOT rolsuper AND NOT rolcreatedb AND NOT rolcreaterole) FROM pg_roles WHERE rolname IN ('acropolis_migrator', 'acropolis_app')", token));
        Assert.Equal("acropolis_migrator", await database.ScalarAsync<string>(
            "SELECT pg_get_userbyid(datdba) FROM pg_database WHERE datname = current_database()", token));
        await using (var migrationIdentity = new NpgsqlConnection(database.MigrationConnection))
        {
            await migrationIdentity.OpenAsync(token);
            await using var identity = new NpgsqlCommand("SELECT current_user", migrationIdentity);
            Assert.Equal("acropolis_migrator", (string)(await identity.ExecuteScalarAsync(token))!);
        }

        var runner = new PlatformMigrationRunner();
        await runner.RunAsync(database.MigrationConnection, token);
        await runner.RunAsync(database.MigrationConnection, token);
        Assert.Equal(1L, await database.ScalarAsync<long>("SELECT count(*) FROM platform.\"__EFMigrationsHistory\"", token));
        Assert.True(await database.ScalarAsync<bool>("SELECT has_table_privilege('acropolis_app', 'platform.\"__EFMigrationsHistory\"', 'SELECT')", token));
        Assert.False(await database.ScalarAsync<bool>("SELECT has_table_privilege('acropolis_app', 'platform.\"__EFMigrationsHistory\"', 'INSERT')", token));

        await using var connection = new NpgsqlConnection(database.RuntimeConnection);
        await connection.OpenAsync(token);
        await using var create = new NpgsqlCommand("CREATE TABLE platform.forbidden(id integer)", connection);
        var rejected = await Assert.ThrowsAsync<PostgresException>(() => create.ExecuteNonQueryAsync(token));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, rejected.SqlState);

        await using var insert = new NpgsqlCommand(
            "INSERT INTO platform.\"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") VALUES ('Forbidden', '10.0.12')", connection);
        var historyRejected = await Assert.ThrowsAsync<PostgresException>(() => insert.ExecuteNonQueryAsync(token));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, historyRejected.SqlState);

        await using var context = new PlatformDbContext(PlatformDbContext.CreateOptions(database.MigrationConnection));
        Assert.False(context.Database.HasPendingModelChanges());
        Assert.Empty(context.Model.GetEntityTypes());
    }

    [Fact]
    public async Task ConcurrentMigrationInvocationsSerializeSafely()
    {
        var token = TestContext.Current.CancellationToken;
        await database.ResetSchemaAsync(token);
        await Task.WhenAll(
            new PlatformMigrationRunner().RunAsync(database.MigrationConnection, token),
            new PlatformMigrationRunner().RunAsync(database.MigrationConnection, token));
        Assert.Equal(1L, await database.ScalarAsync<long>("SELECT count(*) FROM platform.\"__EFMigrationsHistory\"", token));
    }

    [Fact]
    public async Task MissingAndUnknownMigrationsPreventReadiness()
    {
        var token = TestContext.Current.CancellationToken;
        await database.ResetSchemaAsync(token);
        var service = new ReadinessService(new PostgresPlatformStateReader(database.RuntimeConnection), TimeSpan.FromSeconds(2));
        Assert.False((await service.CheckAsync(token)).IsReady);
        await new PlatformMigrationRunner().RunAsync(database.MigrationConnection, token);
        Assert.True((await service.CheckAsync(token)).IsReady);
        await database.ScalarAsync<long>(
            """
            WITH changed AS (
                INSERT INTO platform."__EFMigrationsHistory" ("MigrationId", "ProductVersion")
                VALUES ('20990101000000_Unknown', '10.0.12') RETURNING 1
            ) SELECT count(*) FROM changed
            """, token);
        Assert.False((await service.CheckAsync(token)).IsReady);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new PlatformMigrationRunner().RunAsync(database.MigrationConnection, token));
    }

    [Fact]
    public async Task ForeignObjectsWithoutHistoryArePreservedAndRejected()
    {
        var token = TestContext.Current.CancellationToken;
        await database.ResetSchemaAsync(token);
        await database.ScalarAsync<int>(
            "CREATE TABLE platform.sentinel(id integer); SELECT 1", token);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new PlatformMigrationRunner().RunAsync(database.MigrationConnection, token));
        Assert.True(await database.ScalarAsync<bool>("SELECT to_regclass('platform.sentinel') IS NOT NULL", token));
    }

    [Fact]
    public async Task ApiRemainsLiveDuringRealDatabaseFailureAndRecoversReadiness()
    {
        var token = TestContext.Current.CancellationToken;
        await database.ResetSchemaAsync(token);
        await new PlatformMigrationRunner().RunAsync(database.MigrationConnection, token);
        await new Acropolis.Migrations.ChannelMigrationRunner().RunAsync(database.MigrationConnection, token);
        await using var factory = new DatabaseApiFactory(database.RuntimeConnection);
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready", token)).StatusCode);

        try
        {
            await database.SetConnectionsAllowedAsync(false, token);
            using var ready = await client.GetAsync("/health/ready", token);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
            Assert.Equal("not_ready", (await ready.Content.ReadFromJsonAsync<StatusResponse>(token))!.Status);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health", token)).StatusCode);
        }
        finally
        {
            await database.SetConnectionsAllowedAsync(true, CancellationToken.None);
        }

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready", token)).StatusCode);
    }

    [Fact]
    public async Task MigrationRejectsASchemaOwnedByAnotherRole()
    {
        var token = TestContext.Current.CancellationToken;
        await database.ResetSchemaAsync(token);
        await database.ScalarAsync<int>(
            "DROP SCHEMA platform CASCADE; CREATE SCHEMA platform AUTHORIZATION acropolis_app; SELECT 1", token);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new PlatformMigrationRunner().RunAsync(database.MigrationConnection, token));
    }

    [Fact]
    public async Task WaitingForTheMigrationLockCanBeCancelledWithoutLeakingTheLock()
    {
        var token = TestContext.Current.CancellationToken;
        await database.ResetSchemaAsync(token);
        await using var holder = new NpgsqlConnection(database.AdminConnection);
        await holder.OpenAsync(token);
        await using var acquire = new NpgsqlCommand("SELECT pg_advisory_lock(@key)", holder);
        acquire.Parameters.AddWithValue("key", PlatformMigrationRunner.AdvisoryLockKey);
        await acquire.ExecuteNonQueryAsync(token);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                new PlatformMigrationRunner().RunAsync(database.MigrationConnection, deadline.Token));
        }
        finally
        {
            await using var release = new NpgsqlCommand("SELECT pg_advisory_unlock(@key)", holder);
            release.Parameters.AddWithValue("key", PlatformMigrationRunner.AdvisoryLockKey);
            await release.ExecuteNonQueryAsync(CancellationToken.None);
        }

        await new PlatformMigrationRunner().RunAsync(database.MigrationConnection, token);
        Assert.Equal(1L, await database.ScalarAsync<long>("SELECT count(*) FROM platform.\"__EFMigrationsHistory\"", token));
    }

    private sealed record StatusResponse(string Status);

    private sealed class DatabaseApiFactory(string connectionString) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Database"] = connectionString
                }));
        }
    }
}
