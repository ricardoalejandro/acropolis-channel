using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using Acropolis.Catalog.Infrastructure;
using Acropolis.Subscriptions.Infrastructure;
using Acropolis.Identity.Infrastructure;
using Acropolis.Migrations;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Acropolis.Platform.IntegrationTests;

[Collection("Postgres")]
public sealed class CompositeReadinessTests(PostgresFixture database)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("platform", "missing")]
    [InlineData("identity", "missing")]
    [InlineData("catalog", "missing")]
    [InlineData("subscriptions", "missing")]
    [InlineData("platform", "extra")]
    [InlineData("identity", "extra")]
    [InlineData("catalog", "extra")]
    [InlineData("subscriptions", "extra")]
    [InlineData("platform", "replacement")]
    [InlineData("identity", "replacement")]
    [InlineData("catalog", "replacement")]
    [InlineData("subscriptions", "replacement")]
    public async Task EveryHistoryMustMatchExactlyAndEachProbeReadsTheCurrentDatabase(string module, string change)
    {
        await PrepareAsync();
        await using var factory = new DatabaseFactory(database.RuntimeConnection);
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready", Token)).StatusCode);
        await using var owner = new NpgsqlConnection(database.MigrationConnection);
        await owner.OpenAsync(Token);
        await using var original = new NpgsqlCommand($"SELECT \"MigrationId\", \"ProductVersion\" FROM {module}.\"__EFMigrationsHistory\" ORDER BY \"MigrationId\" LIMIT 1", owner);
        string id;
        string version;
        await using (var row = await original.ExecuteReaderAsync(Token))
        {
            Assert.True(await row.ReadAsync(Token));
            id = row.GetString(0);
            version = row.GetString(1);
        }
        var sql = change switch
        {
            "missing" => $"DELETE FROM {module}.\"__EFMigrationsHistory\" WHERE \"MigrationId\"=@id",
            "extra" => $"INSERT INTO {module}.\"__EFMigrationsHistory\" VALUES ('20990101000000_Unknown','10.0.3')",
            _ => $"UPDATE {module}.\"__EFMigrationsHistory\" SET \"MigrationId\"='20990101000000_Unknown' WHERE \"MigrationId\"=@id"
        };
        await using var mutate = new NpgsqlCommand(sql, owner);
        mutate.Parameters.AddWithValue("id", id);
        await mutate.ExecuteNonQueryAsync(Token);
        using var failed = await client.GetAsync("/health/ready", Token);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        Assert.Equal("""{"status":"not_ready"}""", await failed.Content.ReadAsStringAsync(Token));
        Assert.Equal("history_mismatch", Assert.Single(factory.Logs.Events).Reason);
        var restoreSql = change switch
        {
            "missing" => $"INSERT INTO {module}.\"__EFMigrationsHistory\" VALUES (@id,@version)",
            "extra" => $"DELETE FROM {module}.\"__EFMigrationsHistory\" WHERE \"MigrationId\"='20990101000000_Unknown'",
            _ => $"UPDATE {module}.\"__EFMigrationsHistory\" SET \"MigrationId\"=@id WHERE \"MigrationId\"='20990101000000_Unknown'"
        };
        await using var restore = new NpgsqlCommand(restoreSql, owner);
        restore.Parameters.AddWithValue("id", id);
        restore.Parameters.AddWithValue("version", version);
        await restore.ExecuteNonQueryAsync(Token);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready", Token)).StatusCode);
        Assert.Single(factory.Logs.Events);
    }

    [Theory]
    [InlineData("missing_table", "42P01")]
    [InlineData("permission", "42501")]
    public async Task UnavailableHistoryFailsSafelyAndRecoversWithTheSameHost(string problem, string sqlState)
    {
        await PrepareAsync();
        await using var factory = new DatabaseFactory(database.RuntimeConnection);
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready", Token)).StatusCode);
        var mutation = problem == "missing_table"
            ? "ALTER TABLE catalog.\"__EFMigrationsHistory\" RENAME TO history_backup; SELECT 1"
            : "REVOKE SELECT ON catalog.\"__EFMigrationsHistory\" FROM acropolis_app; SELECT 1";
        await database.ScalarAsync<int>(mutation, Token);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health/ready", Token)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health", Token)).StatusCode);
        var failure = Assert.Single(factory.Logs.Events);
        Assert.Equal("database_error", failure.Reason);
        Assert.Equal("PostgresException", failure.ExceptionType);
        Assert.Equal(sqlState, failure.SqlState);
        var restore = problem == "missing_table"
            ? "ALTER TABLE catalog.history_backup RENAME TO \"__EFMigrationsHistory\"; SELECT 1"
            : "GRANT SELECT ON catalog.\"__EFMigrationsHistory\" TO acropolis_app; SELECT 1";
        await database.ScalarAsync<int>(restore, Token);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready", Token)).StatusCode);
    }

    [Fact]
    public async Task LockedHistoryHonorsTheDeadlineAndReleasesTheConnectionForAFreshProbe()
    {
        await PrepareAsync();
        await using var factory = new DatabaseFactory(database.RuntimeConnection);
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready", Token)).StatusCode);
        await using var owner = new NpgsqlConnection(database.MigrationConnection);
        await owner.OpenAsync(Token);
        await using var transaction = await owner.BeginTransactionAsync(Token);
        await using var command = new NpgsqlCommand("LOCK TABLE catalog.\"__EFMigrationsHistory\" IN ACCESS EXCLUSIVE MODE", owner, transaction);
        await command.ExecuteNonQueryAsync(Token);
        var started = Stopwatch.StartNew();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health/ready", Token)).StatusCode);
        Assert.InRange(started.Elapsed.TotalSeconds, 1, 3.5);
        Assert.Equal("timeout", Assert.Single(factory.Logs.Events).Reason);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health", Token)).StatusCode);
        await transaction.RollbackAsync(Token);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready", Token)).StatusCode);
    }

    [Fact]
    public async Task CancelledHttpProbeIsNotDiagnosedAsDatabaseFailure()
    {
        await PrepareAsync();
        await using var factory = new DatabaseFactory(database.RuntimeConnection);
        using var client = factory.CreateClient();
        await using var owner = new NpgsqlConnection(database.MigrationConnection);
        await owner.OpenAsync(Token);
        await using var transaction = await owner.BeginTransactionAsync(Token);
        await using var command = new NpgsqlCommand("LOCK TABLE catalog.\"__EFMigrationsHistory\" IN ACCESS EXCLUSIVE MODE", owner, transaction);
        await command.ExecuteNonQueryAsync(Token);
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var pending = client.GetAsync("/health/ready", cancelled.Token);
        var observed = false;
        for (var attempt = 0; attempt < 100 && !observed; attempt++)
        {
            observed = await database.ScalarAsync<bool>($"SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE application_name='{factory.ApplicationName}' AND wait_event_type='Lock')", Token);
            if (!observed) await Task.Delay(10, Token);
        }
        Assert.True(observed);
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await transaction.RollbackAsync(Token);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready", Token)).StatusCode);
        Assert.Empty(factory.Logs.Events);
    }

    [Fact]
    public async Task IdentityCatalogAndLiveReadinessShareOneBoundedPool()
    {
        await PrepareAsync();
        await using var factory = new DatabaseFactory(database.RuntimeConnection);
        using var client = factory.CreateClient();
        await using var scope = factory.Services.CreateAsyncScope();
        var source = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        var identityFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<IdentityDbContext>>();
        await using var identity = await identityFactory.CreateDbContextAsync(Token);
        await identity.Database.OpenConnectionAsync(Token);
        var identityId = ((NpgsqlConnection)identity.Database.GetDbConnection()).ProcessID;
        await identity.Database.CloseConnectionAsync();
        var catalog = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        await catalog.Database.OpenConnectionAsync(Token);
        Assert.Equal(identityId, ((NpgsqlConnection)catalog.Database.GetDbConnection()).ProcessID);
        await catalog.Database.CloseConnectionAsync();
        var subscriptions = scope.ServiceProvider.GetRequiredService<SubscriptionsDbContext>();
        await subscriptions.Database.OpenConnectionAsync(Token);
        Assert.Equal(identityId, ((NpgsqlConnection)subscriptions.Database.GetDbConnection()).ProcessID);
        await subscriptions.Database.CloseConnectionAsync();
        var concurrent = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => client.GetAsync("/health/ready", Token)));
        foreach (var response in concurrent)
        {
            using (response) Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        Assert.Equal(1L, await database.ScalarAsync<long>($"SELECT count(*) FROM pg_stat_activity WHERE application_name='{factory.ApplicationName}'", Token));
        await using var secondScope = factory.Services.CreateAsyncScope();
        Assert.Same(source, secondScope.ServiceProvider.GetRequiredService<NpgsqlDataSource>());
        var settings = new NpgsqlConnectionStringBuilder(source.ConnectionString);
        Assert.False(settings.IncludeErrorDetail);
        Assert.Equal(3, settings.Timeout);
        Assert.Equal(3, settings.CommandTimeout);
        Assert.Empty(factory.Logs.Events);
    }

    private async Task PrepareAsync()
    {
        await database.ResetSchemaAsync(Token);
        await new ChannelMigrationRunner().RunAsync(database.MigrationConnection, Token);
    }
    private sealed record Failure(string Reason, string ExceptionType, string SqlState);
    private sealed class CaptureLogs : ILoggerProvider
    {
        public ConcurrentQueue<Failure> Events { get; } = new();
        public ILogger CreateLogger(string categoryName) => new CaptureLogger(categoryName, Events);
        public void Dispose() { }
        private sealed class CaptureLogger(string category, ConcurrentQueue<Failure> events) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (category != "Acropolis.Api.ReadinessDiagnostics" || eventId.Id != 1001) return;
                Assert.Null(exception);
                var values = ((IEnumerable<KeyValuePair<string, object?>>)(object)state!).ToDictionary(pair => pair.Key, pair => pair.Value);
                events.Enqueue(new Failure((string)values["Reason"]!, (string)values["ExceptionType"]!, (string)values["SqlState"]!));
            }
        }
    }
    private sealed class DatabaseFactory : WebApplicationFactory<Program>
    {
        private readonly string connectionString;
        public string ApplicationName { get; } = "acropolis_test_readiness_" + Guid.NewGuid().ToString("N");
        public CaptureLogs Logs { get; } = new();
        public DatabaseFactory(string connectionString)
        {
            this.connectionString = new NpgsqlConnectionStringBuilder(connectionString)
            {
                Pooling = true,
                MaxPoolSize = 1,
                ApplicationName = ApplicationName
            }.ConnectionString;
        }
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Database"] = connectionString,
                ["Identity:EmailEnabled"] = "false"
            }));
            builder.ConfigureTestServices(services => services.AddSingleton<ILoggerProvider>(Logs));
        }
    }
}
